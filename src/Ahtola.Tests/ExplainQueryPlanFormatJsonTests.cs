using Ahtola.Core;
using Ahtola.Core.Storage;
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

    [TestCase("CREATE VIRTUAL TABLE boxes USING rtree(id,min,max);", "SELECT id FROM boxes AS b WHERE min >= 1;", "boxes", "b")]
    [TestCase("CREATE VIRTUAL TABLE docs USING fts5(content);", "SELECT content FROM docs AS d;", "docs", "d")]
    public void VirtualTablePlanIdentifiesItsRowSource(
        string createTable,
        string query,
        string table,
        string alias)
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection, createTable);

        var textDetail = ReadValues(connection, "EXPLAIN QUERY PLAN " + query).Single()[3].AsText();
        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("detail").GetString().Should().Be(textDetail);
        node.GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        node.GetProperty("op").GetProperty("table").GetString().Should().Be(table);
        node.GetProperty("op").GetProperty("alias").GetString().Should().Be(alias);
        node.GetProperty("op").GetProperty("source").GetString().Should().Be("virtual_table");
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
    public void MultiIndexOrPreservesTheTableAlias()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            "CREATE TABLE users(id INTEGER PRIMARY KEY, age INTEGER); " +
            "CREATE INDEX idx_users_age ON users(age);");

        using var document = JsonDocument.Parse(ReadAll(
            connection,
            "EXPLAIN QUERY PLAN FORMAT=JSON SELECT * FROM users AS u WHERE u.id = 5 OR u.age = 30;").Single());
        var op = document.RootElement.GetProperty("nodes")[0].GetProperty("op");
        op.GetProperty("type").GetString().Should().Be("multi_index");
        op.GetProperty("table").GetString().Should().Be("users");
        op.GetProperty("alias").GetString().Should().Be("u");
        op.GetProperty("set_op").GetString().Should().Be("or");
    }

    [Test]
    public void JoinLocalOrDistinctPlanModelsItsDeduplicationStep()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            """
            CREATE TABLE nodes(id TEXT PRIMARY KEY);
            CREATE TABLE edges(src TEXT, dst TEXT, tag INTEGER);
            CREATE INDEX edges_tag ON edges(tag);
            INSERT INTO nodes VALUES ('a'), ('b');
            INSERT INTO edges VALUES ('a', 'a', 7), ('a', 'b', 7), ('b', 'a', 8);
            """);

        const string query = """
            SELECT DISTINCT n.id
            FROM nodes AS n JOIN edges AS e
              ON (n.id = e.src AND e.tag = 7) OR (n.id = e.dst AND e.tag = 7)
            WHERE n.id <> '';
            """;
        var textRows = ReadValues(connection, "EXPLAIN QUERY PLAN " + query);
        textRows.Should().HaveCount(3);
        textRows[0][3].AsText().Should().StartWith("SEARCH e USING INDEX edges_tag");
        textRows[1][3].AsText().Should().StartWith("MULTI-INDEX OR n");
        textRows[2][3].AsText().Should().Be("USE HASH TABLE FOR DISTINCT");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(3);
        nodes[2].GetProperty("detail").GetString().Should().Be(textRows[2][3].AsText());
        nodes[2].GetProperty("op").GetProperty("type").GetString().Should().Be("distinct");
    }

    [Test]
    public void PartialIndexFallbackReportsTheActualBaseTableScan()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(
            connection,
            """
            CREATE TABLE products(id INTEGER PRIMARY KEY, sku TEXT, status TEXT);
            CREATE INDEX active_sku ON products(sku) WHERE status = 'active';
            INSERT INTO products VALUES (1, 'X', 'active'), (2, 'X', 'inactive');
            """);

        const string query = "SELECT p.id FROM products AS p WHERE p.status = 'inactive' AND p.sku = 'X';";
        ReadValues(connection, query).Single()[0].AsInteger().Should().Be(2);
        var detail = ReadValues(connection, "EXPLAIN QUERY PLAN " + query).Single()[3].AsText();
        detail.Should().Be("SCAN p");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("detail").GetString().Should().Be(detail);
        var op = node.GetProperty("op");
        op.GetProperty("type").GetString().Should().Be("scan");
        op.GetProperty("table").GetString().Should().Be("products");
        op.GetProperty("alias").GetString().Should().Be("p");
        op.GetProperty("source").GetString().Should().Be("table");
        op.TryGetProperty("index", out _).Should().BeFalse();
    }

    [Test]
    public void UnmodeledViewAccessDoesNotInventABaseTableScan()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER); CREATE VIEW v AS SELECT id FROM t;");

        const string query = "SELECT id FROM v;";
        ReadValues(connection, "EXPLAIN QUERY PLAN " + query).Single()[3].AsText()
            .Should().StartWith("MANAGED ");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        document.RootElement.GetProperty("result_columns").EnumerateArray()
            .Select(static column => column.GetString()).Should().Equal("id");
        document.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(0);
    }

    [Test]
    public void PlainTableFallbackStillDescribesItsActualBaseScan()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER);");

        const string query = "SELECT id FROM t AS base;";
        ReadValues(connection, "EXPLAIN QUERY PLAN " + query).Single()[3].AsText()
            .Should().StartWith("MANAGED ");
        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        node.GetProperty("op").GetProperty("table").GetString().Should().Be("t");
        node.GetProperty("op").GetProperty("alias").GetString().Should().Be("base");
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

    [TestCase("INSERT INTO items(id, value) VALUES (2, 'new') RETURNING id, value", "id", "value")]
    [TestCase("UPDATE items SET value = 'changed' WHERE id = 1 RETURNING value AS result", "result", null)]
    [TestCase("DELETE FROM items WHERE id = 1 RETURNING *", "id", "value")]
    public void DmlReturningPlanReportsTheStatementResultColumnsWithoutExecutingIt(
        string dml,
        string firstColumn,
        string? secondColumn)
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO items VALUES (1, 'original');");

        using var document = JsonDocument.Parse(
            ReadAll(connection, $"EXPLAIN QUERY PLAN FORMAT=JSON {dml};").Single());
        document.RootElement.GetProperty("result_columns").EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal(secondColumn is null ? [firstColumn] : [firstColumn, secondColumn]);
        ReadValues(connection, "SELECT id, value FROM items").Single()
            .Should().Equal(SqlValue.Integer(1), SqlValue.Text("original"));
    }

    [Test]
    public void NonReturningDmlPlanHasNoResultColumns()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON INSERT INTO items VALUES (1);").Single());
        document.RootElement.GetProperty("result_columns").GetArrayLength().Should().Be(0);
        ReadValues(connection, "SELECT id FROM items").Should().BeEmpty();
    }

    [Test]
    public void UnknownReturningTargetIsNotReportedAsTheTextPlanColumns()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();

        Action explain = () => ReadAll(
            connection, "EXPLAIN QUERY PLAN FORMAT=JSON INSERT INTO missing VALUES (1) RETURNING *;");
        explain.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table: missing*");
    }

    [Test]
    public void CteMaterializationIsOmittedWithoutAPlannedReusableBody()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection, "CREATE TABLE source(id INTEGER PRIMARY KEY, value INTEGER);");

        using var document = JsonDocument.Parse(ReadAll(
            connection,
            """
            EXPLAIN QUERY PLAN FORMAT=JSON
            WITH grouped AS (SELECT value, count(*) AS total FROM source GROUP BY value)
            SELECT a.total FROM source s
            JOIN grouped a ON a.value = s.value
            JOIN grouped b ON b.value = s.id;
            """).Single());
        document.RootElement.TryGetProperty("cte_materializations", out _).Should().BeFalse();
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

    [Test]
    public void CompiledSelfJoinSearchRetainsItsChosenIndexAndRightAlias()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE items(k INTEGER, payload TEXT); " +
            "CREATE INDEX items_k ON items(k); " +
            "INSERT INTO items VALUES (1, 'one'), (2, 'two'); ANALYZE;");

        const string query = """
            SELECT a.payload, b.payload
            FROM items AS a JOIN items AS b INDEXED BY items_k ON a.k = b.k;
            """;
        ReadValues(connection, query).Should().HaveCount(2);
        var details = ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query);
        details.Should().ContainSingle().Which.Should().Be("SEARCH items USING INDEX items_k (k=?)");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("detail").GetString().Should().Be(details[0]);
        var op = node.GetProperty("op");
        op.GetProperty("type").GetString().Should().Be("search");
        op.GetProperty("table").GetString().Should().Be("items");
        op.GetProperty("alias").GetString().Should().Be("b");
        op.GetProperty("join").GetString().Should().Be("inner");
        op.GetProperty("search_kind").GetString().Should().Be("seek");
        op.GetProperty("index").GetProperty("name").GetString().Should().Be("items_k");
        op.GetProperty("index").GetProperty("covering").GetBoolean().Should().BeFalse();
        op.GetProperty("index").GetProperty("ephemeral").GetBoolean().Should().BeFalse();
        op.GetProperty("constraints").EnumerateArray().Select(static value => value.GetString())
            .Should().Equal("k=?");
        var outerScan = document.RootElement.GetProperty("nodes")[1];
        outerScan.GetProperty("detail").GetString().Should().Be("SCAN items AS a");
        outerScan.GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        outerScan.GetProperty("op").GetProperty("alias").GetString().Should().Be("a");
    }

    [Test]
    public void CompiledNonIndexedEquijoinOrdersHashProbeBeforeBuildScanAsTwoRoots()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE outer_items(k INTEGER, payload TEXT); " +
            "CREATE TABLE inner_items(k INTEGER, payload TEXT); " +
            "INSERT INTO outer_items VALUES (1, 'outer-1'), (2, 'outer-2'); " +
            "INSERT INTO inner_items VALUES (1, 'inner-1'), (3, 'inner-3');");

        const string query = """
            SELECT o.payload, i.payload
            FROM outer_items AS o JOIN inner_items AS i NOT INDEXED ON o.k = i.k
            ORDER BY o.k;
            """;
        ReadValues(connection, query).Should().ContainSingle().Which
            .Should().Equal(SqlValue.Text("outer-1"), SqlValue.Text("inner-1"));
        var compiled = ReadValues(connection, "EXPLAIN " + query)
            .Single(row => row[1].AsText() == "OpenJoinCursor")[5].AsText();
        compiled.Should().Contain("equijoin hash-build right");
        ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query)
            .Should().Equal("MANAGED COMPILED VDBE");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(2);
        nodes[0].GetProperty("id").GetInt32().Should().Be(1);
        nodes[0].GetProperty("detail").GetString().Should().Be("HASH JOIN outer_items AS o");
        nodes[0].GetProperty("op").GetProperty("type").GetString().Should().Be("hash_join");
        nodes[0].GetProperty("op").GetProperty("table").GetString().Should().Be("outer_items");
        nodes[0].GetProperty("op").GetProperty("alias").GetString().Should().Be("o");
        nodes[0].GetProperty("op").TryGetProperty("join", out _).Should().BeFalse();
        nodes[0].GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);
        nodes[1].GetProperty("id").GetInt32().Should().Be(2);
        nodes[1].GetProperty("detail").GetString().Should().Be("SCAN inner_items AS i");
        nodes[1].GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        nodes[1].GetProperty("op").GetProperty("table").GetString().Should().Be("inner_items");
        nodes[1].GetProperty("op").GetProperty("alias").GetString().Should().Be("i");
        nodes[1].GetProperty("op").GetProperty("join").GetString().Should().Be("inner");
        nodes[1].GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetRawText().Should().NotContain("hash_build");
        document.RootElement.GetRawText().Should().NotContain("MATERIALIZE hash build input");
    }

    [Test]
    public void CompiledNonEquiLeftJoinReportsTwoScansWithoutInventingHashWork()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE l(k INTEGER); CREATE TABLE r(k INTEGER); " +
            "INSERT INTO l VALUES (1), (3); INSERT INTO r VALUES (2);");
        const string query = """
            SELECT a.k, b.k
            FROM l AS a LEFT JOIN r AS b ON a.k < b.k
            ORDER BY a.k;
            """;
        ReadValues(connection, query).Should().HaveCount(2);
        ReadValues(connection, "EXPLAIN " + query)
            .Should().Contain(row => row[1].AsText() == "OpenJoinCursor");
        ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query)
            .Should().Equal("MANAGED COMPILED VDBE");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(2);
        nodes[0].GetProperty("detail").GetString().Should().Be("SCAN l AS a");
        nodes[0].GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        nodes[1].GetProperty("detail").GetString().Should().Be("SCAN r AS b");
        nodes[1].GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        nodes[1].GetProperty("op").GetProperty("join").GetString().Should().Be("left");
    }

    [Test]
    public void CompiledSelfJoinWithoutIndexKeepsProbeAndBuildAliasesDistinct()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE items(k INTEGER, payload TEXT); " +
            "INSERT INTO items VALUES (1, 'one'), (2, 'two');");

        const string query = """
            SELECT a.payload, b.payload
            FROM items AS a NOT INDEXED JOIN items AS b NOT INDEXED ON a.k = b.k
            ORDER BY a.k;
            """;
        ReadValues(connection, query).Should().HaveCount(2);
        ReadValues(connection, "EXPLAIN " + query)
            .Should().Contain(row => row[1].AsText() == "OpenJoinCursor");
        ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query)
            .Should().Equal("MANAGED COMPILED VDBE");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(2);
        nodes.EnumerateArray().Select(static node => node.GetProperty("op").GetProperty("alias").GetString())
            .Should().BeEquivalentTo(["a", "b"]);
        nodes.EnumerateArray()
            .Select(static node => node.GetProperty("op").GetProperty("table").GetString())
            .Should().OnlyContain(static table => table == "items");
        nodes.EnumerateArray().Select(static node => node.GetProperty("op").GetProperty("type").GetString())
            .Should().BeEquivalentTo(["hash_join", "scan"]);
    }

    [Test]
    public void CompiledHashBuildLeftUsesTheChosenSmallTable()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE small(k INTEGER, label TEXT); " +
            "CREATE TABLE big(k INTEGER, label TEXT); " +
            "INSERT INTO small VALUES (1, 's1'), (2, 's2'), (3, 's3'); " +
            "INSERT INTO big VALUES " +
            string.Join(", ", Enumerable.Range(1, 40).Select(value => $"({value}, 'b{value}')")) +
            "; ANALYZE;");

        const string query = """
            SELECT s.label, b.label
            FROM small AS s JOIN big AS b NOT INDEXED ON s.k = b.k
            ORDER BY s.k;
            """;
        ReadValues(connection, query).Should().HaveCount(3);
        ReadValues(connection, "EXPLAIN " + query)
            .Single(row => row[1].AsText() == "OpenJoinCursor")[5].AsText()
            .Should().Contain("hash-build left");
        ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query)
            .Should().Equal("MANAGED COMPILED VDBE");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(2);
        nodes[0].GetProperty("op").GetProperty("type").GetString().Should().Be("hash_join");
        nodes[0].GetProperty("op").GetProperty("table").GetString().Should().Be("big");
        nodes[0].GetProperty("op").GetProperty("alias").GetString().Should().Be("b");
        nodes[0].GetProperty("op").GetProperty("join").GetString().Should().Be("inner");
        nodes[1].GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        nodes[1].GetProperty("op").GetProperty("table").GetString().Should().Be("small");
        nodes[1].GetProperty("op").GetProperty("alias").GetString().Should().Be("s");
        nodes[1].GetProperty("op").TryGetProperty("join", out _).Should().BeFalse();
        nodes.EnumerateArray().Should().OnlyContain(static node =>
            node.GetProperty("parent").ValueKind == JsonValueKind.Null);
    }

    [Test]
    public void CompiledThreeTableHashBuildLeftNestsTheActualJoinedPrefix()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE small(id INTEGER PRIMARY KEY, label TEXT); " +
            "CREATE TABLE seed(x INTEGER PRIMARY KEY); " +
            "CREATE TABLE big(id INTEGER PRIMARY KEY, label TEXT); " +
            "INSERT INTO small VALUES (1, 's1'), (2, 's2'), (3, 's3'); " +
            "INSERT INTO seed VALUES (1), (2), (3); " +
            "INSERT INTO big VALUES " +
            string.Join(", ", Enumerable.Range(1, 40).Select(value => $"({value}, 'b{value}')")) +
            "; ANALYZE;");

        const string query = """
            SELECT s.label, b.label
            FROM small AS s NOT INDEXED JOIN seed AS e NOT INDEXED ON s.id = e.x
                 JOIN big AS b NOT INDEXED ON s.id = b.id;
            """;
        ReadValues(connection, query).Should().HaveCount(3);
        var compiled = ReadValues(connection, "EXPLAIN " + query)
            .Single(row => row[1].AsText() == "OpenJoinCursor")[5].AsText();
        compiled.Should().Contain("hash-build left");
        compiled.Should().Contain("scan order: s, e, b");
        ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query)
            .Should().Equal("MANAGED COMPILED VDBE");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(4);
        nodes.EnumerateArray().Select(static node => node.GetProperty("id").GetInt32())
            .Should().Equal(1, 2, 3, 4);

        nodes[0].GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);
        nodes[0].GetProperty("detail").GetString().Should()
            .Be("MATERIALIZE hash build input for small AS s");
        nodes[0].GetProperty("op").GetProperty("type").GetString().Should().Be("hash_build");
        nodes[0].GetProperty("op").GetProperty("table").GetString().Should().Be("small");
        nodes[0].GetProperty("op").GetProperty("alias").GetString().Should().Be("s");

        nodes[1].GetProperty("parent").GetInt32().Should().Be(1);
        nodes[1].GetProperty("detail").GetString().Should().Be("HASH JOIN small AS s");
        nodes[1].GetProperty("op").GetProperty("type").GetString().Should().Be("hash_join");
        nodes[1].GetProperty("op").GetProperty("alias").GetString().Should().Be("s");
        nodes[1].GetProperty("op").TryGetProperty("join", out _).Should().BeFalse();

        nodes[2].GetProperty("parent").GetInt32().Should().Be(1);
        nodes[2].GetProperty("detail").GetString().Should().Be("SCAN seed AS e");
        nodes[2].GetProperty("op").GetProperty("type").GetString().Should().Be("scan");
        nodes[2].GetProperty("op").GetProperty("alias").GetString().Should().Be("e");
        nodes[2].GetProperty("op").GetProperty("join").GetString().Should().Be("inner");

        nodes[3].GetProperty("parent").ValueKind.Should().Be(JsonValueKind.Null);
        nodes[3].GetProperty("detail").GetString().Should().Be("HASH JOIN big AS b");
        nodes[3].GetProperty("op").GetProperty("table").GetString().Should().Be("big");
        nodes[3].GetProperty("op").GetProperty("join").GetString().Should().Be("inner");
    }

    [Test]
    public void CompiledThreeTableHashBuildRightDoesNotInventPrefixMaterialization()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE first(k INTEGER); CREATE TABLE middle(k INTEGER); CREATE TABLE last(k INTEGER); " +
            "INSERT INTO first VALUES (1), (2); " +
            "INSERT INTO middle VALUES (1), (2); " +
            "INSERT INTO last VALUES (1), (2);");

        const string query = """
            SELECT a.k, c.k
            FROM first AS a NOT INDEXED JOIN middle AS b NOT INDEXED ON a.k = b.k
                 JOIN last AS c NOT INDEXED ON a.k = c.k;
            """;
        ReadValues(connection, query).Should().HaveCount(2);
        var compiled = ReadValues(connection, "EXPLAIN " + query)
            .Single(row => row[1].AsText() == "OpenJoinCursor")[5].AsText();
        compiled.Should().Contain("hash-build right");
        ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query)
            .Should().Equal("MANAGED COMPILED VDBE");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        document.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(0);
        document.RootElement.GetRawText().Should().NotContain("hash_build");
    }

    [Test]
    public void CompiledJoinAutomaticIndexIsAnEphemeralCoveringSearch()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE outer_items(k INTEGER, payload TEXT); " +
            "CREATE TABLE inner_items(k INTEGER, payload TEXT); " +
            "INSERT INTO outer_items VALUES " +
            string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'o{value}')")) + "; " +
            "INSERT INTO inner_items VALUES " +
            string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'i{value}')")) + "; ANALYZE;");

        const string query = """
            SELECT o.k, i.payload
            FROM outer_items AS o JOIN inner_items AS i ON o.k = i.k
            ORDER BY o.k;
            """;
        ReadValues(connection, query).Should().HaveCount(100);
        var detail = ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query).Single();
        detail.Should().Be("SEARCH inner_items USING AUTOMATIC COVERING INDEX (k=?)");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("detail").GetString().Should().Be(detail);
        var op = node.GetProperty("op");
        op.GetProperty("type").GetString().Should().Be("search");
        op.GetProperty("table").GetString().Should().Be("inner_items");
        op.GetProperty("alias").GetString().Should().Be("i");
        op.GetProperty("join").GetString().Should().Be("inner");
        op.GetProperty("index").GetProperty("name").GetString().Should().Be("automatic_inner_items");
        op.GetProperty("index").GetProperty("covering").GetBoolean().Should().BeTrue();
        op.GetProperty("index").GetProperty("ephemeral").GetBoolean().Should().BeTrue();
        op.GetProperty("constraints")[0].GetString().Should().Be("k=?");
    }

    [Test]
    public void CompiledJoinPagerSeekReportsTheDurableIndexRatherThanAnAutomaticIndex()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var embedded = EmbeddedDatabase.OpenFile("eqp-join-index.db", fileSystem))
        using (var connection = embedded.Connect())
        {
            Execute(connection,
                "CREATE TABLE outer_items(k INTEGER); " +
                "CREATE TABLE inner_items(k INTEGER, payload TEXT); " +
                "CREATE INDEX inner_items_k ON inner_items(k); " +
                "INSERT INTO outer_items VALUES (2), (40); " +
                "INSERT INTO inner_items VALUES " +
                string.Join(", ", Enumerable.Range(1, 100).Select(value => $"({value}, 'p{value}')")) +
                "; ANALYZE;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("eqp-join-index.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        const string query = """
            SELECT i.payload
            FROM outer_items o JOIN inner_items i INDEXED BY inner_items_k
                ON o.k = i.k
            ORDER BY o.k;
            """;
        var detail = ReadPlanDetails(reopenedConnection, "EXPLAIN QUERY PLAN " + query).Single();
        detail.Should().Be("SEARCH inner_items USING INDEX inner_items_k (k=?)");
        using var document = JsonDocument.Parse(
            ReadAll(reopenedConnection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var node = document.RootElement.GetProperty("nodes")[0];
        node.GetProperty("detail").GetString().Should().Be(detail);
        var op = node.GetProperty("op");
        op.GetProperty("type").GetString().Should().Be("search");
        op.GetProperty("table").GetString().Should().Be("inner_items");
        op.GetProperty("alias").GetString().Should().Be("i");
        op.GetProperty("index").GetProperty("name").GetString().Should().Be("inner_items_k");
        op.GetProperty("index").GetProperty("ephemeral").GetBoolean().Should().BeFalse();
        op.GetProperty("constraints")[0].GetString().Should().Be("k=?");

        ReadValues(reopenedConnection, query).Select(static row => row[0].AsText())
            .Should().Equal("p2", "p40");
        reopened.JoinIndexSeekMetrics.DurableCursorPlans.Should().Be(1);
    }

    [Test]
    public void CompiledDerivedJoinSeekRemainsUnmodeledWithoutBaseTableMetadata()
    {
        using var embedded = new EmbeddedDatabase();
        using var connection = embedded.Connect();
        Execute(connection,
            "CREATE TABLE outer_items(k INTEGER); " +
            "CREATE TABLE inner_items(k INTEGER, value INTEGER); " +
            "INSERT INTO outer_items VALUES (1), (2); " +
            "INSERT INTO inner_items VALUES (1, 10), (2, 20);");

        const string query = """
            SELECT o.k, d.total
            FROM outer_items o JOIN
                (SELECT k, sum(value) AS total FROM inner_items GROUP BY k) d
                ON o.k = d.k;
            """;
        ReadValues(connection, query).Should().HaveCount(2);
        var details = ReadPlanDetails(connection, "EXPLAIN QUERY PLAN " + query);
        details.Should().ContainSingle().Which.Should().StartWith("SEARCH d USING INDEX derived_");

        using var document = JsonDocument.Parse(
            ReadAll(connection, "EXPLAIN QUERY PLAN FORMAT=JSON " + query).Single());
        var nodes = document.RootElement.GetProperty("nodes");
        nodes.GetArrayLength().Should().Be(1);
        var node = nodes[0];
        node.GetProperty("detail").GetString().Should().Be(details[0]);
        node.GetProperty("op").GetProperty("type").GetString().Should().Be("unmodeled");
        node.GetProperty("op").GetProperty("detail").GetString().Should().Be(details[0]);
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
