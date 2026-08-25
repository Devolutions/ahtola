using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedVectorIndexTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Planning for the vector method: which query shapes it serves, which it declines, and the proof
/// that the plan EXPLAIN QUERY PLAN advertises is the plan that produces the rows.
/// </summary>
public sealed class ManagedVectorIndexPlannerTests
{
    private const int CorpusSize = 600;
    private const int Dimensions = 8;
    private const string Query = "vector32('[1.5,-2.5,3.5,0.5,-1.5,2.5,-0.5,1.0]')";

    private static EmbeddedConnection Seed(EmbeddedDatabase database, VectorTestMetric metric = VectorTestMetric.L2)
    {
        var connection = database.Connect();
        SeedCorpus(
            connection,
            GenerateClusteredVectors(CorpusSize, Dimensions, seed: 606060),
            VectorTestEncoding.Float32,
            metric,
            Dimensions);
        return connection;
    }

    [Test]
    public void TheAdvertisedPlanIsTheExecutedPlan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";

        var detail = ExplainDetail(connection, sql);
        detail.Should().Contain("USING INDEX METHOD vector INDEX docs_knn")
            .And.Contain("pattern=KnnLimit")
            .And.Contain("metric=l2")
            .And.Contain("encoding=float32")
            .And.Contain("lists=64")
            .And.Contain("probes=")
            .And.Contain("exact=1");

        var before = EmbeddedDatabase.MethodIndexScansExecuted;
        QueryIntegers(connection, sql).Should().HaveCount(5);
        EmbeddedDatabase.MethodIndexScansExecuted.Should().Be(before + 1);
    }

    [Test]
    public void BothArgumentOrdersAndTheAliasFormAreRecognized()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var expected = QueryIntegers(
            connection,
            $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;");

        foreach (var sql in new[]
                 {
                     $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;",
                     $"SELECT id FROM docs ORDER BY vector_distance_l2({Query}, embedding) LIMIT 5;",
                     $"SELECT id, vector_distance_l2(embedding, {Query}) AS d FROM docs ORDER BY d LIMIT 5;",
                     $"SELECT id FROM docs AS x ORDER BY vector_distance_l2(x.embedding, {Query}) LIMIT 5;",
                 })
        {
            ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector", sql);
            var rows = ManagedVectorIndexTestHarness.Query(connection, sql);
            rows.Select(static row => row[0].AsInteger()).Should().Equal(expected, sql);
        }
    }

    [Test]
    public void AnUnlimitedRankingRetainsEveryRowAndLosesToTheScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query});";

        // KNN without a limit removes nothing, so it can only ever be more expensive than the scan
        // it would replace, and the planner has to refuse it.
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD vector");
        QueryIntegers(connection, sql).Should().HaveCount(CorpusSize);
    }

    [TestCaseSource(nameof(DeclinedShapes))]
    public void DeclinedShapesFallBackToTheScanWithIdenticalRows(string indexed, string scanned)
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);

        ExplainDetail(connection, indexed).Should().NotContain("INDEX METHOD vector", indexed);
        ManagedVectorIndexTestHarness.Query(connection, indexed)
            .Select(static row => row[0].AsInteger())
            .Should().Equal(
                ManagedVectorIndexTestHarness.Query(connection, scanned).Select(static row => row[0].AsInteger()),
                indexed);
    }

    private static IEnumerable<TestCaseData> DeclinedShapes()
    {
        (string Name, string Indexed)[] shapes =
        [
            ("Descending", $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) DESC LIMIT 5;"),
            ("SecondOrderTerm", $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}), id LIMIT 5;"),
            ("WrongMetric", $"SELECT id FROM docs ORDER BY vector_distance_cos(embedding, {Query}) LIMIT 5;"),
            ("RowDependentQuery", "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, embedding) LIMIT 5;"),
            ("ResidualPredicate", $"SELECT id FROM docs WHERE id > 100 ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;"),
            ("Distinct", $"SELECT DISTINCT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;"),
            ("GroupBy", $"SELECT id FROM docs GROUP BY id ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;"),
            ("NoLimit", $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query});"),
            ("CollatedOrdering", $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) COLLATE NOCASE LIMIT 5;"),
        ];

        foreach (var (name, indexed) in shapes)
            yield return new TestCaseData(indexed, indexed.Replace("FROM docs", "FROM plain", StringComparison.Ordinal)).SetName(name);
    }

    [Test]
    public void ANonLiteralLimitIsNotPushedDown()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);

        // The cut is unknown at plan time, so the method must not be allowed to truncate to it.
        ExplainDetail(
                connection,
                $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT (SELECT 5);")
            .Should().NotContain("INDEX METHOD vector");
    }

    [Test]
    public void AShadowedDistanceCallbackSuppressesPlanning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");

        connection.RegisterScalarFunction("vector_distance_l2", -1, static _ => SqlValue.Real(0.0));
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ShadowingAnyOwnedDistanceNameSuppressesPlanning()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");

        // An L2 index still declines when a different distance name is shadowed: the engine has to
        // agree with the scalar evaluator about every call in the statement, not just this one.
        connection.RegisterScalarFunction("vector_distance_dot", -1, static _ => SqlValue.Real(0.0));
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void AnIndexOnAnotherTableNeverBindsToThisSource()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        Execute(connection, "CREATE TABLE other(id INTEGER PRIMARY KEY, embedding BLOB);");

        ExplainDetail(
                connection,
                $"SELECT id FROM docs ORDER BY vector_distance_l2(other.embedding, {Query}) LIMIT 5;")
            .Should().NotContain("INDEX METHOD vector");
    }

    [Test]
    public void ScalarErrorSemanticsSurviveThePlan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);

        foreach (var (bad, expected) in new (string, string)[]
                 {
                     ("'not a vector'", "Invalid vector"),
                     ("NULL", "Invalid vector type"),
                     ("vector32('[1,2,3]')", "Vectors must have the same dimensions"),
                     ("vector64('[1,2,3,4,5,6,7,8]')", "Vectors must be of the same type"),
                     ("42", "Invalid vector type"),
                 })
        {
            var indexed = ShouldThrow(
                connection,
                $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {bad}) LIMIT 5;");
            var scanned = ShouldThrow(
                connection,
                $"SELECT id FROM plain ORDER BY vector_distance_l2(embedding, {bad}) LIMIT 5;");

            indexed.Message.Should().Contain(expected);

            // Not merely "an error": the same error the unindexed scan raises.
            indexed.Message.Should().Be(scanned.Message);
        }
    }

    [Test]
    public void ARowWhoseColumnIsNotAValidVectorDisablesThePlanEntirely()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");

        // A NULL embedding makes the scalar form of this query raise on the row it reaches. An index
        // that skipped the row would turn that error into a result set, so the plan is declined and
        // the scan answers — errors included.
        Execute(connection, "INSERT INTO docs VALUES (100001, NULL);");
        Execute(connection, "INSERT INTO plain VALUES (100001, NULL);");
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD vector");
        ShouldThrow(connection, sql).Message
            .Should().Be(ShouldThrow(connection, sql.Replace("FROM docs", "FROM plain", StringComparison.Ordinal)).Message);

        Execute(connection, "DELETE FROM docs WHERE id = 100001;");
        ExplainDetail(connection, sql).Should().Contain("INDEX METHOD vector");
    }

    [Test]
    public void AWrongDimensionRowAlsoDisablesThePlan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";

        Execute(connection, "INSERT INTO docs VALUES (100002, vector32('[1,2,3]'));");
        Execute(connection, "INSERT INTO plain VALUES (100002, vector32('[1,2,3]'));");
        ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD vector");
        ShouldThrow(connection, sql).Message
            .Should().Be(ShouldThrow(connection, sql.Replace("FROM docs", "FROM plain", StringComparison.Ordinal)).Message);
    }

    [Test]
    public void JoinsAndSubqueriesNeverTruncate()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        Execute(connection, "CREATE TABLE tags(id INTEGER PRIMARY KEY, label TEXT);");
        Execute(connection, "INSERT INTO tags SELECT id, 'tag' FROM docs;");

        var joined = QueryIntegers(
            connection,
            $"SELECT d.id FROM docs d JOIN tags t ON t.id = d.id ORDER BY vector_distance_l2(d.embedding, {Query}) LIMIT 5;");
        var scanned = QueryIntegers(
            connection,
            $"SELECT d.id FROM plain d JOIN tags t ON t.id = d.id ORDER BY vector_distance_l2(d.embedding, {Query}) LIMIT 5;");
        joined.Should().Equal(scanned);

        var nested = QueryIntegers(
            connection,
            $"SELECT id FROM (SELECT id, embedding FROM docs) ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;");
        nested.Should().Equal(scanned);
    }

    [Test]
    public void MvccOverlayStatementsFallBackToTheScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = Seed(database);
        Execute(connection, "PRAGMA journal_mode = mvcc;");
        Execute(connection, "BEGIN CONCURRENT;");
        try
        {
            var sql = $"SELECT id FROM docs ORDER BY vector_distance_l2(embedding, {Query}) LIMIT 5;";
            ExplainDetail(connection, sql).Should().NotContain("INDEX METHOD vector");
            QueryIntegers(connection, sql).Should().HaveCount(5);
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }
}
