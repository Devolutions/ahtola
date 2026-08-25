using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the planner half of the review: a semi/anti-join rewrite must apply the same
/// fallible-expression guard to <c>EXISTS</c> that it applies to <c>IN</c>, a registered collation
/// callback counts as fallible, and a method-index query argument is hoisted out of the per-row
/// scalar path only when it is recursively deterministic and callback-free.
/// </summary>
public sealed class PlannerRewriteReviewRegressionTests
{
    // -------------------------------------------------------------------------------------------
    // EXISTS gets the IN guard: a fallible term inside the subquery declines the rewrite.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void AFallibleSubqueryPredicateDeclinesTheExistsRewriteEvenAsTheOnlyConjunct()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedJsonCorpus(connection);
        database.ResetRewriteDiagnostics();

        // A single conjunct has nothing to short-circuit against, so the statement-level reordering
        // guard does not apply: only the subquery's own predicate can decline this rewrite. The
        // inner WHERE decides which inner rows are ever inspected, and a semi-join reaches a
        // different set of them.
        var rows = () => QueryIntegers(
            connection,
            "SELECT o.id FROM outer_rows AS o WHERE EXISTS ("
            + "SELECT 1 FROM inner_rows AS i WHERE i.k = o.k AND json_extract(i.j, '$.a') = 1) ORDER BY o.id;");

        rows.Should().NotThrow().Which.Should().Equal(1L);
        database.RewriteDiagnostics.SemiJoins.Should().Be(
            0,
            "a subquery predicate that can fail on its input declines the rewrite");
    }

    [Test]
    public void AMalformedJsonRowInsideAnExistsSubqueryDoesNotGainAnError()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedJsonCorpus(connection);
        database.ResetRewriteDiagnostics();

        // AND stops at the first false, so the subquery never runs for the outer row the first term
        // rejects. A semi-join runs before every remaining WHERE term and for every outer row.
        var rows = () => QueryIntegers(
            connection,
            "SELECT o.id FROM outer_rows AS o WHERE o.k = 1 AND EXISTS ("
            + "SELECT 1 FROM inner_rows AS i WHERE i.k = o.k AND json_extract(i.j, '$.a') = 1) ORDER BY o.id;");

        rows.Should().NotThrow().Which.Should().Equal(1L);
        database.RewriteDiagnostics.SemiJoins.Should().Be(0);
    }

    [Test]
    public void AMalformedJsonRowInsideAnInSubqueryDoesNotGainAnError()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedJsonCorpus(connection);
        database.ResetRewriteDiagnostics();

        var rows = () => QueryIntegers(
            connection,
            "SELECT o.id FROM outer_rows AS o WHERE o.k = 1 AND o.k IN ("
            + "SELECT i.k FROM inner_rows AS i WHERE json_extract(i.j, '$.a') = 1) ORDER BY o.id;");

        rows.Should().Throw<EmbeddedSqlException>("the un-rewritten IN evaluates its subquery to completion");
        database.RewriteDiagnostics.SemiJoins.Should().Be(0);
    }

    [Test]
    public void ACorrelatedExistsWithoutFallibleTermsIsStillUnnested()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE inner_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "INSERT INTO outer_rows VALUES (1, 1), (2, 2), (3, 3);");
        Execute(connection, "INSERT INTO inner_rows VALUES (1, 1), (2, 1), (3, 3);");
        database.ResetRewriteDiagnostics();

        // The guard must not make every EXISTS decline: with no fallible term in sight the rewrite
        // is still available and still answers exactly one row per outer match.
        QueryIntegers(
                connection,
                "SELECT o.id FROM outer_rows AS o WHERE o.k > 0 AND EXISTS ("
                + "SELECT 1 FROM inner_rows AS i WHERE i.k = o.k) ORDER BY o.id;")
            .Should().Equal(1L, 3L);
        database.RewriteDiagnostics.SemiJoins.Should().BeGreaterThan(0);
    }

    // -------------------------------------------------------------------------------------------
    // A registered collation is a managed callback: it can throw, so it counts as fallible.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void AThrowingCollationInsideAnExistsSubqueryDeclinesTheRewrite()
    {
        using var database = new EmbeddedDatabase();
        database.RegisterCollation("EXPLODING", static (left, right) =>
            left.Contains("boom", StringComparison.Ordinal) || right.Contains("boom", StringComparison.Ordinal)
                ? throw new EmbeddedSqlException("collation exploded")
                : string.CompareOrdinal(left, right));

        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE inner_rows(id INTEGER PRIMARY KEY, k INTEGER, t TEXT);");
        Execute(connection, "INSERT INTO outer_rows VALUES (1, 1), (2, 2);");
        Execute(connection, "INSERT INTO inner_rows VALUES (1, 1, 'safe'), (2, 99, 'boom');");
        database.ResetRewriteDiagnostics();

        // COLLATE dispatches to arbitrary managed code that can throw, so it is fallible exactly the
        // way a scalar function is — and the rewrite would change which rows it runs against.
        var rows = () => QueryIntegers(
            connection,
            "SELECT o.id FROM outer_rows AS o WHERE EXISTS ("
            + "SELECT 1 FROM inner_rows AS i WHERE i.k = o.k AND i.t COLLATE EXPLODING > 'a') ORDER BY o.id;");

        rows.Should().NotThrow().Which.Should().Equal(1L);
        database.RewriteDiagnostics.SemiJoins.Should().Be(
            0,
            "a comparison that can dispatch to a registered collation callback can fail on its input");
    }

    [Test]
    public void AThrowingCollationInsideAnInSubqueryDeclinesTheRewrite()
    {
        using var database = new EmbeddedDatabase();
        database.RegisterCollation("EXPLODING", static (left, right) =>
            left.Contains("boom", StringComparison.Ordinal) || right.Contains("boom", StringComparison.Ordinal)
                ? throw new EmbeddedSqlException("collation exploded")
                : string.CompareOrdinal(left, right));

        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE inner_rows(id INTEGER PRIMARY KEY, k INTEGER, t TEXT);");
        Execute(connection, "INSERT INTO outer_rows VALUES (1, 1), (2, 9);");
        Execute(connection, "INSERT INTO inner_rows VALUES (1, 1, 'safe'), (2, 9, 'boom');");
        database.ResetRewriteDiagnostics();

        var rows = () => QueryIntegers(
            connection,
            "SELECT o.id FROM outer_rows AS o WHERE o.k = 1 AND o.k IN ("
            + "SELECT i.k FROM inner_rows AS i WHERE i.t COLLATE EXPLODING > 'a') ORDER BY o.id;");

        rows.Should().Throw<EmbeddedSqlException>().WithMessage("*collation exploded*");
        database.RewriteDiagnostics.SemiJoins.Should().Be(0);
    }

    [Test]
    public void ARegisteredCollationDeclinesTheRewriteWithoutChangingTheAnswer()
    {
        using var database = new EmbeddedDatabase();
        database.RegisterCollation("REVERSED", static (left, right) => string.CompareOrdinal(right, left));

        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE outer_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE inner_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "INSERT INTO outer_rows VALUES (1, 1), (2, 2), (3, 3);");
        Execute(connection, "INSERT INTO inner_rows VALUES (1, 1), (2, 3);");
        database.ResetRewriteDiagnostics();

        // Declining the rewrite is a plan decision, never an answer change.
        QueryIntegers(
                connection,
                "SELECT o.id FROM outer_rows AS o WHERE o.k > 0 AND EXISTS ("
                + "SELECT 1 FROM inner_rows AS i WHERE i.k = o.k) ORDER BY o.id;")
            .Should().Equal(1L, 3L);
        database.RewriteDiagnostics.SemiJoins.Should().Be(
            0,
            "once any custom collation exists a comparison may dispatch to a callback");
    }

    // -------------------------------------------------------------------------------------------
    // Method-index query arguments: hoisting requires recursive determinism and no callbacks.
    // -------------------------------------------------------------------------------------------

    [Test]
    public void ANonDeterministicFtsQueryArgumentStaysOnTheScalarPath()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // random() is evaluated once per row by the scalar evaluator. Hoisting it would evaluate it
        // once for the whole scan, which is a different query.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, CAST(random() AS TEXT));")
            .Should().NotContain("INDEX METHOD");

        // A deterministic wrapper does not launder it.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'a' || CAST(abs(random()) AS TEXT));")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ACustomNoColumnScalarFunctionStaysOnTheScalarPathForFts()
    {
        using var database = new EmbeddedDatabase();
        var calls = 0;
        database.RegisterScalarFunction("pick_query", 0, _ =>
        {
            calls++;
            return SqlValue.Text("term7");
        });

        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // A registered callback takes no columns, so the old row-dependence test called it
        // hoistable. It is arbitrary managed code: it may answer differently per call and its
        // failure is a per-row event.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, pick_query());")
            .Should().NotContain("INDEX METHOD");

        calls = 0;
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, pick_query()) ORDER BY id;")
            .Should().NotBeEmpty();
        calls.Should().BeGreaterThan(1, "the scalar path evaluates the callback per row");
    }

    [Test]
    public void ACustomNoColumnScalarFunctionStaysOnTheScalarPathForVector()
    {
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "pick_vector",
            0,
            _ => ManagedVectorRegressionSupport.QueryVectorValue());

        using var connection = database.Connect();
        ManagedVectorRegressionSupport.SeedVectorCorpus(connection);

        ExplainDetail(connection, "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, pick_vector()) LIMIT 5;")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ANonDeterministicVectorQueryArgumentStaysOnTheScalarPath()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ManagedVectorRegressionSupport.SeedVectorCorpus(connection);

        ExplainDetail(
                connection,
                "SELECT id FROM docs ORDER BY vector_distance_l2(embedding, vector32('[' || CAST(abs(random()) % 2 AS TEXT) || ',0,0,1]')) LIMIT 5;")
            .Should().NotContain("INDEX METHOD");
    }

    [Test]
    public void ADeterministicHoistableArgumentStillUsesTheMethodIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // The guard must not disable hoisting outright: a literal, a parameter, and a deterministic
        // built-in over literals all remain evaluable once per scan.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'term7');")
            .Should().Contain("INDEX METHOD");
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, lower('TERM7'));")
            .Should().Contain("INDEX METHOD");
    }

    [Test]
    public void ARegisteredCallbackShadowingADeterministicBuiltinBlocksHoisting()
    {
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("lower", 1, arguments => SqlValue.Text("term7"));

        using var connection = database.Connect();
        SeedLargeCorpus(connection);

        // The callback replaces the built-in for every call in the statement, so the name says
        // nothing about purity any more.
        ExplainDetail(connection, "SELECT id FROM docs WHERE fts_match(title, body, lower('TERM7'));")
            .Should().NotContain("INDEX METHOD");
    }

    private static void SeedJsonCorpus(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE outer_rows(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE inner_rows(id INTEGER PRIMARY KEY, k INTEGER, j TEXT);");
        Execute(connection, "INSERT INTO outer_rows VALUES (1, 1), (2, 2);");

        // The malformed row sits on a key no outer row carries, so the statement as written never
        // evaluates json_extract against it and has no error to lose or gain.
        Execute(connection, "INSERT INTO inner_rows VALUES (1, 1, '{\"a\":1}'), (2, 99, 'not json at all');");
    }

    /// <summary>The last EXPLAIN QUERY PLAN detail line, which is the access-path row.</summary>
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

/// <summary>Vector corpus helpers shared by the planner regression suite.</summary>
internal static class ManagedVectorRegressionSupport
{
    private const int Dimensions = 4;

    public static void SeedVectorCorpus(EmbeddedConnection connection)
    {
        ManagedVectorIndexTestHarness.Execute(
            connection,
            "CREATE TABLE docs(id INTEGER PRIMARY KEY, embedding BLOB);");
        ManagedVectorIndexTestHarness.Execute(
            connection,
            $"CREATE INDEX docs_vec ON docs USING vector (embedding) "
            + $"WITH (dims = {Dimensions}, lists = 8, min_rows = 8);");

        var vectors = ManagedVectorIndexTestHarness.GenerateClusteredVectors(
            600,
            Dimensions,
            seed: 4242,
            clusters: 6);
        ManagedVectorIndexTestHarness.Execute(connection, "BEGIN;");
        for (var index = 0; index < vectors.Length; index++)
        {
            ManagedVectorIndexTestHarness.Execute(
                connection,
                $"INSERT INTO docs VALUES ({index + 1}, vector32('{ManagedVectorIndexTestHarness.Literal(vectors[index])}'));");
        }

        ManagedVectorIndexTestHarness.Execute(connection, "COMMIT;");
    }

    public static SqlValue QueryVectorValue()
    {
        var bytes = new byte[Dimensions * sizeof(float)];
        double[] values = [0.1, 0.2, 0.3, 0.4];
        for (var index = 0; index < values.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float)),
                BitConverter.SingleToInt32Bits((float)values[index]));
        }

        return SqlValue.Blob(bytes);
    }
}
