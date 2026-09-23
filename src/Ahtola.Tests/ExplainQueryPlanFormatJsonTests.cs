using Ahtola.Core;
using AwesomeAssertions;
using System.Text.Json;

namespace Ahtola.Tests;

/// <summary>
/// Verifies <c>EXPLAIN QUERY PLAN FORMAT=JSON</c>: the parser accepts the optional
/// case-insensitive <c>FORMAT=JSON</c>/<c>FORMAT=TEXT</c> clause after
/// <c>EXPLAIN QUERY PLAN</c> (mirroring <c>parse_explain_query_plan_format</c> in
/// <c>turso-src/sqlite/parser/src/parser.rs</c>), and the JSON format emits one
/// <c>plan_json</c> TEXT row whose envelope matches the documented transport shape
/// (<c>turso-src/docs/eqp-json.md</c>): version 1, the statement SQL, result columns,
/// and one node per plan row with id/parent/detail.
/// </summary>
public class ExplainQueryPlanFormatJsonTests
{
    [Test]
    public void FormatJsonEmitsOnePlanJsonRowWithDocumentedEnvelope()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users (id INTEGER PRIMARY KEY, age INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age);");

        var rows = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON SELECT id FROM users WHERE age > 21;");
        rows.Should().HaveCount(1);
        var json = rows[0];
        json.Should().StartWith("{\"version\":1,");
        // The envelope echoes the statement exactly as written, FORMAT=JSON clause and trailing
        // ';' both included (turso-src/docs/eqp-json.md; core/translate/eqp.rs's `sql` field is
        // the verbatim input, terminator included -- SqlScript.Split retains it and
        // SqlParser.Parse already tolerated one trailing semicolon via
        // Consume(TokenKind.Semicolon) before Expect(TokenKind.End)).
        json.Should().Contain("\"sql\":\"EXPLAIN QUERY PLAN FORMAT=JSON SELECT id FROM users WHERE age > 21;\"");
        json.Should().Contain("\"result_columns\":[");
        json.Should().Contain("\"nodes\":[");
        json.Should().Contain("\"detail\":\"");
        json.Should().EndWith("}");
    }

    [Test]
    public void FormatJsonEchoesTheOriginalStatementRatherThanReconstructingIt()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        var sql = """
            explain   query plan format=json
              SELECT 1;
            """;
        var json = ReadAll(connection, sql);

        using var document = JsonDocument.Parse(json.Single());
        document.RootElement.GetProperty("sql").GetString().Should().Be(sql);
    }

    [Test]
    public void FormatClauseIsCaseInsensitiveAndTextIsDefault()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        // Lowercase format keyword and value both parse.
        var json = ReadAll(connection, "explain query plan format=json SELECT 1;");
        json.Should().HaveCount(1);
        json[0].Should().Contain("\"version\":1");

        // TEXT (explicit) keeps the classic four-column rows.
        var text = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=TEXT SELECT 1;");
        text.Should().HaveCount(1);
        text[0].Should().NotContain("\"version\":1");

        // Omitting the clause defaults to TEXT.
        var omitted = ReadAll(connection, "EXPLAIN QUERY PLAN SELECT 1;");
        omitted.Should().HaveCount(1);
        omitted[0].Should().NotContain("\"version\":1");
    }

    [Test]
    public void UnknownFormatIsRejected()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        var statement = () => connection.PrepareScript("EXPLAIN QUERY PLAN FORMAT=YAML SELECT 1;").ToList();
        statement.Should().Throw<EmbeddedSqlException>()
            .Which.Message.Should().Contain(
                "unknown EXPLAIN QUERY PLAN format: YAML (supported formats: TEXT, JSON)");
    }

    [Test]
    public void NodeEntriesCarryThePlanDetailText()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE t(a INTEGER PRIMARY KEY, b INTEGER); " +
            "CREATE INDEX idx_t_b ON t(b);");

        var json = ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON SELECT a FROM t WHERE b = 1;");
        json.Should().HaveCount(1);
        // A modeled node (here, an index SEARCH) carries a non-empty detail drawn from the
        // engine's plan text. A statement with no per-step plan modeled yet reports an empty
        // nodes array instead of a fabricated node (see ExecuteExplainQueryPlan's
        // isPlaceholderOnly handling) rather than a fake "detail".
        json[0].Should().MatchRegex("\"detail\":\"[^\"]+\"");
        json[0].Should().Contain("\"id\":");
        json[0].Should().Contain("\"parent\":");
    }

    [Test]
    public void CancellationSafeLeftJoinReportsItsExecutedIndexAccessPaths()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            """
            CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER, amount REAL);
            CREATE INDEX idx_users_age ON users(age);
            CREATE INDEX idx_orders_user ON orders(user_id);
            INSERT INTO users VALUES (1, 'Ada', 30), (2, 'Bo', 20);
            INSERT INTO orders VALUES (1, 1, 12.5);
            """);

        var rows = ReadValues(
            connection,
            """
            SELECT u.name, o.amount
            FROM users u
            LEFT JOIN orders o ON o.user_id = u.id
            WHERE u.age > 21
            ORDER BY o.amount;
            """);
        rows.Should().ContainSingle().Which.Should().Equal(SqlValue.Text("Ada"), SqlValue.Real(12.5));

        using var document = JsonDocument.Parse(
            ReadAll(
                connection,
                """
                EXPLAIN QUERY PLAN FORMAT=JSON
                SELECT u.name, o.amount
                FROM users u
                LEFT JOIN orders o ON o.user_id = u.id
                WHERE u.age > 21
                ORDER BY o.amount;
                """).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes[0].GetProperty("op").GetProperty("index").GetProperty("name").GetString().Should().Be("idx_users_age");
        nodes[0].GetProperty("op").GetProperty("constraints")[0].GetString().Should().Be("age>?");
        nodes[1].GetProperty("op").GetProperty("index").GetProperty("name").GetString().Should().Be("idx_orders_user");
        nodes[1].GetProperty("op").GetProperty("join").GetString().Should().Be("left");
        nodes[2].GetProperty("op").GetProperty("type").GetString().Should().Be("order_by");
    }

    [Test]
    public void MultiIndexOrIncludesTheRowidPrimaryKeyBranch()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users(id INTEGER PRIMARY KEY, age INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age);");

        var json = ReadAll(
            connection,
            "EXPLAIN QUERY PLAN FORMAT=JSON SELECT * FROM users WHERE id = 5 OR age = 30;");

        using var document = JsonDocument.Parse(json.Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("detail").GetString()
            .Should().Be("MULTI-INDEX OR users (PRIMARY KEY, idx_users_age)");
        var indexes = node.GetProperty("op").GetProperty("indexes");
        indexes.EnumerateArray().Select(static index => index.GetString())
            .Should().Equal("PRIMARY KEY", "idx_users_age");
    }

    [Test]
    public void DerivedSourceNestsItsIndexScanUnderTheCoroutineNode()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users(id INTEGER PRIMARY KEY, city_id INTEGER, age INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            SELECT city_id, avg(age)
            FROM (SELECT * FROM users WHERE age > 18)
            GROUP BY city_id
            ORDER BY avg(age);
            """).Single();

        using var document = JsonDocument.Parse(json);
        var nodes = document.RootElement.GetProperty("nodes");
        nodes[0].GetProperty("detail").GetString().Should().Be("SCAN (subquery-0)");
        nodes[0].GetProperty("op").GetProperty("source").GetString().Should().Be("subquery");
        nodes[1].GetProperty("parent").GetInt32().Should().Be(nodes[0].GetProperty("id").GetInt32());
        nodes[1].GetProperty("op").GetProperty("index").GetProperty("name").GetString().Should().Be("idx_users_age");
    }

    [Test]
    public void UnionPlanNestsBothArmsAndIdentifiesItsTemporaryBtree()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT, age INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            SELECT name FROM users WHERE age > 30
            UNION
            SELECT name FROM users
            ORDER BY name;
            """).Single();

        using var document = JsonDocument.Parse(json);
        var nodes = document.RootElement.GetProperty("nodes");
        nodes[0].GetProperty("op").GetProperty("type").GetString().Should().Be("compound");
        nodes[1].GetProperty("parent").GetInt32().Should().Be(nodes[0].GetProperty("id").GetInt32());
        nodes[3].GetProperty("op").GetProperty("op").GetString().Should().Be("union");
        nodes[3].GetProperty("op").GetProperty("temp_btree").GetBoolean().Should().BeTrue();
        nodes[4].GetProperty("parent").GetInt32().Should().Be(nodes[3].GetProperty("id").GetInt32());
    }

    [Test]
    public void RecursiveCtePlanReportsSetupAndRecursiveStep()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            WITH RECURSIVE cnt(x) AS (
                SELECT 1
                UNION ALL
                SELECT x + 1 FROM cnt WHERE x < 10
            )
            SELECT x FROM cnt;
            """).Single();

        using var document = JsonDocument.Parse(json);
        var nodes = document.RootElement.GetProperty("nodes");
        nodes[0].GetProperty("op").GetProperty("subquery").GetProperty("recursive").GetBoolean().Should().BeTrue();
        nodes[1].GetProperty("op").GetProperty("type").GetString().Should().Be("recursive_setup");
        nodes[3].GetProperty("op").GetProperty("type").GetString().Should().Be("recursive_step");
        nodes[4].GetProperty("parent").GetInt32().Should().Be(nodes[3].GetProperty("id").GetInt32());
        nodes[4].GetProperty("op").GetProperty("source").GetString().Should().Be("recursive_cte_input");
    }

    [Test]
    public void ListAndScalarSubqueriesKeepTheirDistinctCorrelationLifetimes()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT, age INTEGER, city_id INTEGER); " +
            "CREATE TABLE orders(id INTEGER PRIMARY KEY, user_id INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age); " +
            "CREATE INDEX idx_orders_user ON orders(user_id);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            SELECT u.name, (SELECT count(*) FROM orders o WHERE o.user_id = u.id)
            FROM users u
            WHERE u.age IN (SELECT age FROM users WHERE city_id = 3);
            """).Single();

        using var document = JsonDocument.Parse(json);
        var nodes = document.RootElement.GetProperty("nodes");
        nodes[0].GetProperty("op").GetProperty("correlated").GetBoolean().Should().BeFalse();
        nodes[2].GetProperty("op").GetProperty("search_kind").GetString().Should().Be("in_seek");
        nodes[3].GetProperty("op").GetProperty("correlated").GetBoolean().Should().BeTrue();
        nodes[4].GetProperty("parent").GetInt32().Should().Be(nodes[3].GetProperty("id").GetInt32());
    }

    [Test]
    public void SharedCteReadersReferenceOneMaterialization()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT, city_id INTEGER); " +
            "CREATE TABLE orders(id INTEGER PRIMARY KEY, user_id INTEGER, amount INTEGER); " +
            "CREATE INDEX idx_orders_user ON orders(user_id);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            WITH spenders AS (
                SELECT user_id, sum(amount) AS total FROM orders GROUP BY user_id
            )
            SELECT u.name, s1.total
            FROM users u
            JOIN spenders s1 ON s1.user_id = u.id
            JOIN spenders s2 ON s2.user_id = u.city_id;
            """).Single();

        using var document = JsonDocument.Parse(json);
        var materialization = document.RootElement.GetProperty("cte_materializations")[0];
        materialization.GetProperty("name").GetString().Should().Be("spenders");
        materialization.GetProperty("nodes")[0].GetInt32().Should().Be(2);
        var nodes = document.RootElement.GetProperty("nodes");
        nodes[2].GetProperty("op").GetProperty("subquery").GetProperty("execution").GetString()
            .Should().Be("materialized_reuse");
        nodes[3].GetProperty("op").GetProperty("subquery").GetProperty("cte_id").GetInt32().Should().Be(0);
    }

    [TestCase("a > 1", "a>?")]
    [TestCase("a < 3", "a<?")]
    public void RangeIndexSearchKeepsItsOperatorInTextAndJson(string predicate, string constraint)
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE ranges(id INTEGER PRIMARY KEY, a INTEGER); " +
            "CREATE INDEX idx_ranges_a ON ranges(a NULLS LAST);");

        var text = ReadPlanDetails(connection, $"EXPLAIN QUERY PLAN SELECT id FROM ranges WHERE {predicate};");
        var json = ReadAll(
            connection,
            $"EXPLAIN QUERY PLAN FORMAT=JSON SELECT id FROM ranges WHERE {predicate};");

        text.Should().ContainSingle().Which.Should().Contain($"({constraint})");
        json.Should().ContainSingle().Which.Should().Contain($"\"constraints\":[\"{constraint}\"]");
    }

    /// <summary>
    /// A hand-written LEFT JOIN with a non-equality condition, grouped by the outer rowid and
    /// filtered by HAVING, has exactly the same AST shape TryRewriteAggregateJoinFirst produces
    /// for its own rewrite -- but it was never produced by that rewrite (it is what the user
    /// wrote), and a hash join cannot execute a "&gt;" condition at all. The describer must not
    /// mistake this shape for its own rewrite's output and must not claim "HASH JOIN" here.
    /// </summary>
    [Test]
    public void HandWrittenNonEquiJoinLeftJoinIsNotMisdescribedAsHashJoin()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE outer_t(id INTEGER PRIMARY KEY, k INTEGER); " +
            "CREATE TABLE inner_t(k INTEGER, x INTEGER);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            SELECT o.id
            FROM outer_t o
            LEFT JOIN inner_t i ON i.k > o.k
            GROUP BY o.rowid
            HAVING count(i.x) > 0;
            """);
        json.Should().HaveCount(1);
        json[0].Should().NotContain("hash_join");
        json[0].Should().NotContain("HASH JOIN");
    }

    /// <summary>
    /// A correlated aggregate subquery that stays correlated (unnest.rs declines every rewrite)
    /// still routes its outer table through ExecuteSelect's ordinary TryPlanManagedIndexScan
    /// planner, so an indexable predicate on the outer table's own WHERE genuinely executes as
    /// an index SEARCH, not a hardcoded full SCAN.
    /// </summary>
    [Test]
    public void StaysCorrelatedDescriberUsesRealIndexPlanForOuterTable()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE idx_outer(id INTEGER PRIMARY KEY, k INTEGER, age INTEGER); " +
            "CREATE INDEX idx_outer_age ON idx_outer(age); " +
            "CREATE TABLE nondet_inner(k INTEGER, x INTEGER);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            SELECT o.id
            FROM idx_outer o
            WHERE o.age > 21
              AND o.id + (random() % 2) = o.id
              AND o.k > (
                  SELECT sum(i.x)
                  FROM nondet_inner i
                  WHERE i.k = o.k
              );
            """);
        json.Should().HaveCount(1);
        json[0].Should().Contain("\"detail\":\"SEARCH o USING INDEX idx_outer_age");
        json[0].Should().Contain("\"index\":{\"name\":\"idx_outer_age\"");
    }

    /// <summary>
    /// EXISTS/NOT EXISTS unnested into an internal semi/anti join use the declared correlation
    /// index when its key is safe for the evaluator's lookup rules. The JSON plan must describe
    /// that same real access path rather than the fallback automatic hash lookup.
    /// </summary>
    [Test]
    public void ExistsSemiJoinDescribesTheDeclaredIndex()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE semi_users (id INTEGER PRIMARY KEY, name TEXT); " +
            "CREATE TABLE semi_orders (id INTEGER PRIMARY KEY, user_id INTEGER); " +
            "CREATE INDEX idx_semi_orders_user ON semi_orders(user_id);");

        var json = ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            SELECT *
            FROM semi_users u
            WHERE EXISTS (SELECT 1 FROM semi_orders o WHERE o.user_id = u.id);
            """);
        json.Should().HaveCount(1);
        json[0].Should().Contain("\"detail\":\"SCAN semi_users AS u\"");
        json[0].Should().Contain(
            "\"detail\":\"SEARCH o USING COVERING INDEX idx_semi_orders_user (user_id=?)\",\"op\":{\"type\":\"search\",\"table\":\"semi_orders\",\"alias\":\"o\",\"join\":\"semi\"");
        json[0].Should().Contain("\"index\":{\"name\":\"idx_semi_orders_user\",\"covering\":true,\"ephemeral\":false}");
        json[0].Should().NotContain("\"integer_primary_key\"");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static List<string> ReadAll(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                    rows.Add(statement.GetValue(0).Kind == SqlValueKind.Text
                        ? statement.GetValue(0).AsText()
                        : statement.GetValue(0).ToString() ?? "");
            }
        }

        return rows;
    }

    private static List<SqlValue[]> ReadValues(EmbeddedConnection connection, string sql)
    {
        var rows = new List<SqlValue[]>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                    rows.Add(Enumerable.Range(0, statement.ColumnCount).Select(statement.GetValue).ToArray());
            }
        }

        return rows;
    }

    private static List<string> ReadPlanDetails(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                    rows.Add(statement.GetValue(3).AsText());
            }
        }

        return rows;
    }
}
