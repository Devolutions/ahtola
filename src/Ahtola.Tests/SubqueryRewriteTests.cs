using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Coverage for the pure AST rewrite stage in <c>EmbeddedDatabase.SubqueryRewrites.cs</c>: the
/// FROM-clause derived-table flattening and the correlated EXISTS / NOT EXISTS / IN unnesting
/// into the internal semi and anti joins.
/// <para>
/// Every correctness assertion is differential against Microsoft.Data.Sqlite, so a rewrite that
/// changed an answer fails even when both engines agree with each other's bug. The rewrite
/// counters exposed by <see cref="EmbeddedDatabase.RewriteDiagnostics"/> then prove which route
/// actually ran: an "eligible" test asserts the rewrite fired, and an "excluded" test asserts it
/// did not, so a gate that silently stops working is caught rather than hidden behind a passing
/// result comparison.
/// </para>
/// </summary>
public sealed class SubqueryRewriteTests
{
    private const string OrdersSetup =
        """
        CREATE TABLE customers(id INTEGER PRIMARY KEY, name TEXT, region TEXT);
        CREATE TABLE orders(id INTEGER PRIMARY KEY, customer_id INTEGER, total INTEGER);
        INSERT INTO customers VALUES
            (1,'ada','north'),
            (2,'bob','south'),
            (3,'cyd','north'),
            (4,'dee',NULL);
        INSERT INTO orders VALUES
            (10,1,100),
            (11,1,250),
            (12,1,250),
            (13,2,50),
            (14,NULL,7);
        """;

    // ------------------------------------------------------------------------------------
    // FROM-clause derived-table flattening.
    // ------------------------------------------------------------------------------------

    [Test]
    public void FlattensSimpleDerivedTableAndKeepsSqliteResults()
    {
        AssertMatchesSqlite(
            OrdersSetup,
            "SELECT id, label FROM (SELECT id AS id, name AS label FROM customers) WHERE id > 1 ORDER BY id;");

        AssertRewrites(
            OrdersSetup,
            "SELECT id, label FROM (SELECT id AS id, name AS label FROM customers) WHERE id > 1 ORDER BY id;",
            flattened: 1);
    }

    [Test]
    public void FlattensAliasedDerivedTableWithQualifiedReferences()
    {
        const string query =
            """
            SELECT d.label, d.total
            FROM (SELECT name AS label, total AS total FROM customers, orders WHERE customers.id = orders.customer_id) AS d
            WHERE d.total >= 100
            ORDER BY d.label, d.total;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, flattened: 1);
    }

    [Test]
    public void FlattensStarProjectionsAndPreservesColumnNames()
    {
        const string query = "SELECT * FROM (SELECT id, name AS who FROM customers) ORDER BY id;";

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, flattened: 1);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, OrdersSetup);
        ColumnNames(connection, query).Should().Equal("id", "who");

        // A bare `SELECT x` projection keeps the visible name `x`, not the name of the inner
        // expression it is replaced with.
        ColumnNames(connection, "SELECT who FROM (SELECT name AS who FROM customers);")
            .Should().Equal("who");
    }

    [Test]
    public void FlattensNestedDerivedTablesRepeatedly()
    {
        const string query =
            """
            SELECT v FROM (SELECT u AS v FROM (SELECT total AS u FROM orders) WHERE u > 60) ORDER BY v;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        // The inner-most select is flattened while planning the intermediate derived table, and
        // the intermediate one while planning the outer select.
        AssertRewrites(OrdersSetup, query, flattened: 2);
    }

    [Test]
    public void FlattenedPredicatesKeepDeclaredCollationAndAffinity()
    {
        const string setup =
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, code TEXT COLLATE NOCASE, num INTEGER);
            INSERT INTO t VALUES (1,'Alpha',5),(2,'ALPHA',7),(3,'beta',9);
            """;

        // The derived column carries `t.code`'s NOCASE collation through the substitution, so
        // the outer equality still matches all three spellings.
        AssertMatchesSqlite(setup, "SELECT id FROM (SELECT id, code AS c FROM t) WHERE c = 'alpha' ORDER BY id;");
        AssertRewrites(setup, "SELECT id FROM (SELECT id, code AS c FROM t) WHERE c = 'alpha' ORDER BY id;", flattened: 1);

        // INTEGER affinity is applied to the text literal on the other side of the comparison.
        AssertMatchesSqlite(setup, "SELECT id FROM (SELECT id, num AS n FROM t) WHERE n = '7';");
        AssertMatchesSqlite(setup, "SELECT id FROM (SELECT id, CAST(num AS TEXT) AS n FROM t) WHERE n = '7';");
        AssertMatchesSqlite(
            setup,
            "SELECT id FROM (SELECT id, code COLLATE BINARY AS c FROM t) WHERE c = 'alpha' ORDER BY id;");
    }

    /// <summary>
    /// SQLite lets a bare WHERE name fall back to a projection alias of the same SELECT when it
    /// names no source column, so the inner filter of
    /// <c>(SELECT a AS x FROM t WHERE x &gt; 0)</c> means <c>a &gt; 0</c>. Hoisting that clause
    /// verbatim would re-expose it to the <em>enclosing</em> SELECT's alias list, where the same
    /// name means something else — or nothing at all.
    /// </summary>
    [Test]
    public void FlattenedInnerWhereBindsInnerProjectionAliases()
    {
        const string setup =
            """
            CREATE TABLE t(a INTEGER, b INTEGER);
            INSERT INTO t VALUES (-1,10),(1,20),(2,30);
            """;

        // The exact regression: the enclosing SELECT has no `x` at all, so an unbound copy of
        // the inner WHERE is a "no such column: x" error.
        const string exact = "SELECT x FROM (SELECT a AS x FROM t WHERE x > 0) ORDER BY x;";
        AssertMatchesSqlite(setup, exact);
        AssertRewrites(setup, exact, flattened: 1);

        // The enclosing SELECT aliases the same name to a *different* expression, so an unbound
        // copy would silently filter on `-a > 0` instead of `a > 0`.
        const string shadowed = "SELECT -x AS x FROM (SELECT a AS x FROM t WHERE x > 0) ORDER BY 1;";
        AssertMatchesSqlite(setup, shadowed);
        AssertRewrites(setup, shadowed, flattened: 1);

        // Canonical-first: a name that is *both* a source column and a projection alias is the
        // source column, in the inner SELECT and after the hoist alike.
        const string sourceWins = "SELECT v FROM (SELECT b AS a, a AS v FROM t WHERE a > 0) ORDER BY v;";
        AssertMatchesSqlite(setup, sourceWins);
        AssertRewrites(setup, sourceWins, flattened: 1);

        // The alias expression is substituted, not the alias name, so a computed alias keeps
        // its meaning under the hoist.
        const string computed = "SELECT s FROM (SELECT a + b AS s FROM t WHERE s > 15) ORDER BY s;";
        AssertMatchesSqlite(setup, computed);
        AssertRewrites(setup, computed, flattened: 1);

        // Aliases referenced from the inner WHERE spend the same duplication budget as the
        // enclosing clauses: `r` would be drawn twice, so the rewrite declines.
        const string nondeterministic = "SELECT r FROM (SELECT abs(random()) AS r FROM t WHERE r > 0);";
        AssertRewrites(setup, nondeterministic, flattened: 0);

        // A bare name the derived table itself rejects still has to be rejected, not answered
        // by the enclosing alias list.
        AssertFailsLikeSqlite(
            setup,
            "SELECT a AS zz FROM (SELECT a FROM t WHERE zz > 0);",
            "no such column: zz");
    }

    /// <summary>
    /// A USING/NATURAL join publishes <c>COALESCE(left, right)</c> for each joined name, which a
    /// star expansion can only reproduce as the raw left column. That is exact while the left
    /// side can never be NULL-extended (INNER/LEFT), and lossy under RIGHT/FULL, where the
    /// coalesced column must report the surviving right value.
    /// </summary>
    [Test]
    public void DoesNotFlattenStarOverCoalescedRightOrFullJoinOutput()
    {
        const string setup =
            """
            CREATE TABLE a(k INTEGER, av TEXT);
            CREATE TABLE b(k INTEGER, bv TEXT);
            INSERT INTO a VALUES (1,'a1'),(2,'a2');
            INSERT INTO b VALUES (2,'b2'),(3,'b3');
            """;

        var declined = new[]
        {
            "SELECT * FROM (SELECT * FROM a RIGHT JOIN b USING (k)) ORDER BY k;",
            "SELECT * FROM (SELECT * FROM a RIGHT OUTER JOIN b USING (k)) AS d ORDER BY d.k;",
            "SELECT * FROM (SELECT * FROM a NATURAL RIGHT JOIN b) ORDER BY k;",
            "SELECT * FROM (SELECT * FROM a NATURAL FULL JOIN b) ORDER BY k;",
            // `a.*` reports the coalesced joined column too (only a hand-written `a.k` reads the
            // raw left slot), so a qualified star is just as lossy here.
            "SELECT * FROM (SELECT a.*, b.bv FROM a RIGHT JOIN b USING (k)) ORDER BY bv;",
        };

        foreach (var query in declined)
        {
            AssertMatchesSqlite(setup, query);
            AssertRewrites(setup, query, flattened: 0);
        }

        // `FULL JOIN … USING` is an engine-wide gap (it needs an explicit ON equality), and the
        // nested form must report it rather than flatten into a silently NULL-padded answer.
        const string fullUsing = "SELECT * FROM (SELECT * FROM a FULL JOIN b USING (k)) ORDER BY k;";
        AssertRewrites(setup, fullUsing, flattened: 0, expectFailure: true);

        // INNER and LEFT keep flattening: an unmatched left row's joined column is NULL on both
        // sides, so the raw left column already equals the coalesced value.
        var flattened = new[]
        {
            "SELECT * FROM (SELECT * FROM a JOIN b USING (k)) ORDER BY k;",
            "SELECT * FROM (SELECT * FROM a LEFT JOIN b USING (k)) ORDER BY k;",
            "SELECT * FROM (SELECT * FROM a NATURAL JOIN b) ORDER BY k;",
            "SELECT * FROM (SELECT * FROM a NATURAL LEFT JOIN b) ORDER BY k;",
        };

        foreach (var query in flattened)
        {
            AssertMatchesSqlite(setup, query);
            AssertRewrites(setup, query, flattened: 1);
        }

        // A projection that names the joined column directly stays an unqualified reference,
        // which still resolves through the coalesced column after the hoist.
        const string bareCoalescedColumn = "SELECT * FROM (SELECT k, bv FROM a RIGHT JOIN b USING (k)) ORDER BY k;";
        AssertMatchesSqlite(setup, bareCoalescedColumn);
        AssertRewrites(setup, bareCoalescedColumn, flattened: 1);

        // A hand-written qualified reference means the raw left slot in SQLite, and keeps
        // meaning that after the hoist.
        const string qualifiedColumn = "SELECT * FROM (SELECT a.k AS ak, bv FROM a RIGHT JOIN b USING (k)) ORDER BY bv;";
        AssertMatchesSqlite(setup, qualifiedColumn);
        AssertRewrites(setup, qualifiedColumn, flattened: 1);
    }

    [Test]
    public void DoesNotFlattenCardinalityOrOrderChangingSubqueries()
    {
        var excluded = new[]
        {
            // DISTINCT removes duplicates before the outer filter sees them.
            "SELECT total FROM (SELECT DISTINCT total AS total FROM orders) WHERE total > 60 ORDER BY total;",
            // LIMIT/OFFSET pick a prefix of the inner result.
            "SELECT total FROM (SELECT total AS total FROM orders LIMIT 2) WHERE total > 60;",
            "SELECT total FROM (SELECT total AS total FROM orders LIMIT 5 OFFSET 1) WHERE total > 60;",
            // ORDER BY only matters with a limit, but it is still not preserved by flattening.
            "SELECT total FROM (SELECT total AS total FROM orders ORDER BY total DESC) WHERE total > 60;",
            // GROUP BY / HAVING / aggregates change row identity.
            "SELECT c, n FROM (SELECT customer_id AS c, COUNT(*) AS n FROM orders GROUP BY customer_id) WHERE n > 1;",
            "SELECT n FROM (SELECT COUNT(*) AS n FROM orders) WHERE n > 1;",
            "SELECT c FROM (SELECT customer_id AS c FROM orders GROUP BY customer_id HAVING COUNT(*) > 1) WHERE c > 0;",
            // A window function annotates the inner rows.
            "SELECT r FROM (SELECT row_number() OVER (ORDER BY id) AS r FROM orders) WHERE r > 1;",
            // A compound select is not a SelectStatement at all.
            "SELECT total FROM (SELECT total FROM orders UNION ALL SELECT total FROM orders) WHERE total > 60;",
            // A WITH body may hold a multi-use or MATERIALIZED CTE that flattening would duplicate.
            "SELECT total FROM (WITH c AS (SELECT total FROM orders) SELECT total FROM c) WHERE total > 60;",
            "SELECT total FROM (WITH c AS MATERIALIZED (SELECT total FROM orders) SELECT total FROM c) WHERE total > 60;",
        };

        foreach (var query in excluded)
        {
            AssertMatchesSqlite(OrdersSetup, query);
            AssertRewrites(OrdersSetup, query, flattened: 0);
        }
    }

    [Test]
    public void DoesNotFlattenOuterJoinOrMultiSourceFromClauses()
    {
        // The derived table must be the whole FROM clause; as one arm of a join, hoisting it
        // would move the enclosing WHERE across the join's null padding.
        const string joined =
            """
            SELECT c.id
            FROM customers AS c LEFT JOIN (SELECT customer_id AS cid FROM orders) AS d ON d.cid = c.id
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, joined);
        AssertRewrites(OrdersSetup, joined, flattened: 0);
    }

    [Test]
    public void DoesNotDuplicateNonDeterministicProjections()
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1),(2),(3);";

        // Two substitution sites (the result column and the WHERE term) would evaluate the
        // clock twice where the derived table read it once.
        AssertRewrites(
            setup,
            "SELECT r FROM (SELECT CURRENT_TIMESTAMP AS r FROM t) WHERE r > '';",
            flattened: 0);

        // A star expansion is a substitution site too, so `*, r` is still two uses.
        AssertRewrites(setup, "SELECT *, r FROM (SELECT CURRENT_TIMESTAMP AS r FROM t);", flattened: 0);

        // Referenced exactly once, substitution cannot duplicate the evaluation, so it is
        // allowed — through a named reference or through a star.
        AssertRewrites(setup, "SELECT r FROM (SELECT CURRENT_TIMESTAMP AS r FROM t);", flattened: 1);
        AssertRewrites(setup, "SELECT * FROM (SELECT CURRENT_TIMESTAMP AS r FROM t);", flattened: 1);
    }

    [Test]
    public void DoesNotFlattenProjectionsThatCallFunctions()
    {
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t VALUES (1,'a'),(2,'b');";

        // SQLite drops a value's JSON subtype at the FROM-clause boundary, and only a function
        // result can carry or propagate one. Flattening would remove the boundary, so any
        // function-bearing projection declines.
        AssertMatchesSqlite(setup, "SELECT json_array(v) FROM (SELECT json_object('a', id) AS v FROM t);");
        AssertRewrites(setup, "SELECT json_array(v) FROM (SELECT json_object('a', id) AS v FROM t);", flattened: 0);
        AssertMatchesSqlite(setup, "SELECT n FROM (SELECT upper(name) AS n FROM t) WHERE n > 'A' ORDER BY n;");
        AssertRewrites(setup, "SELECT n FROM (SELECT upper(name) AS n FROM t) WHERE n > 'A' ORDER BY n;", flattened: 0);

        // A user-registered function is opaque to every classifier here and takes the same
        // conservative route.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        connection.RegisterScalarFunction("opaque", 1, args => args[0]);
        Query(connection, "SELECT v FROM (SELECT opaque(id) AS v FROM t) WHERE v > 1;")
            .Should().HaveCount(1);
        database.RewriteDiagnostics.FlattenedFromSubqueries.Should().Be(0);
    }

    [Test]
    public void DoesNotFlattenJsonArrowProjections()
    {
        const string setup =
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, j TEXT);
            INSERT INTO t VALUES (1,'{"a":[1,2]}');
            """;

        // `->` yields a JSON-subtyped value. The derived table strips that subtype at the
        // FROM-clause boundary, so `json_array(v)` quotes the text; flattening removes the
        // boundary and `json_array(j -> '$.a')` would nest the array instead. `->` is a binary
        // operator, not a function call, so the function-based guard never saw it.
        const string arrow = "SELECT json_array(v) FROM (SELECT j -> '$.a' AS v FROM t);";
        AssertMatchesSqlite(setup, arrow);
        AssertRewrites(setup, arrow, flattened: 0);

        // `->>` returns a plain SQL value today, but it is the same JSON path machinery and is
        // treated as subtype-sensitive too rather than relying on that staying true.
        const string arrowText = "SELECT json_array(v) FROM (SELECT j ->> '$.a' AS v FROM t);";
        AssertMatchesSqlite(setup, arrowText);
        AssertRewrites(setup, arrowText, flattened: 0);

        // Nested inside a larger expression, and behind a CASE, the operator still declines.
        AssertRewrites(setup, "SELECT v FROM (SELECT (j -> '$.a') || '' AS v FROM t);", flattened: 0);
        AssertRewrites(
            setup,
            "SELECT v FROM (SELECT CASE WHEN id > 0 THEN j -> '$.a' END AS v FROM t);",
            flattened: 0);

        // Malformed JSON must still raise on both engines, with or without an outer filter.
        const string malformed =
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, j TEXT);
            INSERT INTO t VALUES (1,'{"a":1}'),(2,'nope');
            """;
        AssertFailsLikeSqlite(malformed, "SELECT v FROM (SELECT j -> '$.a' AS v FROM t);", "malformed JSON");
        AssertFailsLikeSqlite(
            malformed,
            "SELECT v FROM (SELECT j -> '$.a' AS v FROM t) WHERE v IS NOT NULL;",
            "malformed JSON");
        AssertFailsLikeSqlite(
            malformed,
            "SELECT json_array(v) FROM (SELECT j ->> '$.a' AS v FROM t) WHERE v IS NOT NULL;",
            "malformed JSON");
    }

    [Test]
    public void DoesNotFlattenWhenTheInnerSourceShadowsACorrelatedQualifier()
    {
        // `x` is the outer `t AS x` inside the subquery, because the inner `u AS x` lives in the
        // derived table's own scope. Hoisting `u AS x` into the subquery's FROM clause makes
        // `x.v` resolve to the hoisted table instead, turning `d.w = x.v` into `v = v`.
        const string setup =
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER);
            CREATE TABLE u(id INTEGER PRIMARY KEY, v INTEGER);
            INSERT INTO t VALUES (1,10),(2,20);
            INSERT INTO u VALUES (1,10),(2,99);
            """;

        var queries = new[]
        {
            "SELECT x.id FROM t AS x WHERE EXISTS (SELECT 1 FROM (SELECT v AS w FROM u AS x) AS d WHERE d.w = x.v) ORDER BY x.id;",
            "SELECT x.id, (SELECT count(*) FROM (SELECT v AS w FROM u AS x) AS d WHERE d.w = x.v) FROM t AS x ORDER BY x.id;",
            // Unaliased inner source: the bare table name is the shadowing qualifier.
            "SELECT u.id FROM u WHERE EXISTS (SELECT 1 FROM (SELECT v AS w FROM u) AS d WHERE d.w > u.v) ORDER BY u.id;",
            // The shadowed reference sits in a projection rather than a WHERE.
            "SELECT (SELECT max(d.w) + x.v FROM (SELECT v AS w FROM u AS x) AS d) FROM t AS x ORDER BY x.id;",
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(setup, query);
            AssertRewrites(setup, query, flattened: 0);
        }

        // A correlated qualifier that does *not* collide with the hoisted source still flattens,
        // so the guard is a collision check and not a blanket ban on correlation. The subquery
        // is planned once per outer row, so only "more than zero" is a stable expectation.
        const string safe =
            "SELECT y.id FROM t AS y WHERE EXISTS (SELECT 1 FROM (SELECT v AS w FROM u AS x) AS d WHERE d.w = y.v) ORDER BY y.id;";
        AssertMatchesSqlite(setup, safe);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        database.ResetRewriteDiagnostics();
        Query(connection, safe);
        database.RewriteDiagnostics.FlattenedFromSubqueries.Should().BeGreaterThan(0);
    }

    [Test]
    public void DoesNotFlattenWhenANestedSubqueryReferencesTheDerivedTable()
    {
        const string query =
            """
            SELECT d.id FROM (SELECT id AS id FROM customers) AS d
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = d.id)
            ORDER BY d.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        // Flattening declines, but the derived table is still a perfectly good outer side for
        // the semi-join, so the correlated EXISTS is still unnested.
        AssertRewrites(OrdersSetup, query, flattened: 0, semiJoins: 1);
    }

    [Test]
    public void SemiJoinWorksOverAMultiTableOuterFromClause()
    {
        const string query =
            """
            SELECT c.id, o.id FROM customers AS c JOIN orders AS o ON o.customer_id = c.id
            WHERE EXISTS (SELECT 1 FROM customers AS x WHERE x.id = c.id AND x.region = 'north')
            ORDER BY c.id, o.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 1);
    }

    [Test]
    public void SemiJoinHonoursALimitPushedIntoTheRowSource()
    {
        // With the EXISTS conjunct consumed the WHERE clause becomes empty, so the LIMIT is
        // pushed into the row source. The semi-join loop must stop after that many surviving
        // outer rows, not after that many candidates.
        const string query =
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id) LIMIT 1;";

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 1);
    }

    [Test]
    public void FlatteningAndUnnestingAreIndependentStagesOnTheSameStatement()
    {
        // The EXISTS term references the derived table, so the flattening stage declines (it
        // would have to rewrite `d.k` inside the subquery's own scope). The unnesting stage
        // still fires, using the un-flattened derived table as the semi-join's outer side, so
        // one stage declining never blocks the other.
        const string query =
            """
            SELECT k FROM (SELECT id AS k, region AS r FROM customers) AS d
            WHERE d.r = 'north' AND EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = d.k)
            ORDER BY k;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, flattened: 0, semiJoins: 1);
    }

    [Test]
    public void DoesNotFlattenAmbiguousDerivedColumnNames()
    {
        const string query =
            """
            SELECT d.id FROM (SELECT customers.id, orders.id FROM customers, orders) AS d ORDER BY d.id;
            """;

        // Two visible columns share the name `id`, so a by-name substitution would be a guess.
        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, flattened: 0);
    }

    [Test]
    public void FlatteningNeverMasksAnUnresolvableColumnReference()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, OrdersSetup);

        // `name` is not a column of the derived table; flattening would make it resolve against
        // the hoisted base table. Validation runs first, so the error is preserved.
        var act = () => Query(connection, "SELECT c FROM (SELECT id AS c FROM customers) WHERE name = 'ada';");
        act.Should().Throw<EmbeddedSqlException>();
        database.RewriteDiagnostics.FlattenedFromSubqueries.Should().Be(0);
    }

    // ------------------------------------------------------------------------------------
    // Correlated EXISTS / NOT EXISTS -> semi / anti join.
    // ------------------------------------------------------------------------------------

    [Test]
    public void CorrelatedExistsBecomesASemiJoinWithoutMultiplyingOuterRows()
    {
        // customer 1 has three orders. A semi-join must emit the customer once; an inner join
        // would emit it three times.
        const string query =
            """
            SELECT c.id, c.name FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 1);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, OrdersSetup);
        Query(connection, query).Should().HaveCount(2);
        ReadScalar(connection, $"SELECT COUNT(*) FROM ({query.TrimEnd(';', '\n', '\r', ' ')});")
            .Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void CorrelatedNotExistsBecomesAnAntiJoin()
    {
        const string query =
            """
            SELECT c.id FROM customers AS c
            WHERE NOT EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, antiJoins: 1);
    }

    [Test]
    public void SemiAndAntiJoinsPreserveNullComparisonSemantics()
    {
        // orders row 14 has a NULL customer_id and customers row 4 has a NULL region. `=` is
        // never true for NULL, so neither side ever matches through those rows.
        AssertMatchesSqlite(
            OrdersSetup,
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id AND o.total = 7)
            ORDER BY c.id;
            """);

        AssertMatchesSqlite(
            OrdersSetup,
            """
            SELECT o.id FROM orders AS o
            WHERE NOT EXISTS (SELECT 1 FROM customers AS c WHERE c.id = o.customer_id)
            ORDER BY o.id;
            """);

        AssertMatchesSqlite(
            OrdersSetup,
            """
            SELECT c.id FROM customers AS c
            WHERE NOT EXISTS (SELECT 1 FROM customers AS x WHERE x.region = c.region AND x.id <> c.id)
            ORDER BY c.id;
            """);
    }

    [Test]
    public void SemiJoinKeepsInnerNameShadowingAndCollation()
    {
        const string setup =
            """
            CREATE TABLE a(id INTEGER PRIMARY KEY, code TEXT COLLATE NOCASE);
            CREATE TABLE b(id INTEGER PRIMARY KEY, code TEXT);
            INSERT INTO a VALUES (1,'Alpha'),(2,'beta'),(3,'Gamma');
            INSERT INTO b VALUES (1,'ALPHA'),(2,'BETA');
            """;

        // `code` inside the subquery binds to b, exactly as it does before the rewrite; the
        // comparison then uses a.code's NOCASE collation for the correlated equality.
        const string query =
            """
            SELECT a.id FROM a
            WHERE EXISTS (SELECT 1 FROM b WHERE code = a.code)
            ORDER BY a.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, semiJoins: 1);

        // Self-referencing alias shadowing: the inner `a` hides the outer one inside the
        // subquery, and the rewrite must keep that binding.
        const string shadowed =
            """
            SELECT a.id FROM a
            WHERE EXISTS (SELECT 1 FROM b AS a WHERE a.id = 1)
            ORDER BY a.id;
            """;
        AssertMatchesSqlite(setup, shadowed);
    }

    [Test]
    public void SemiJoinComposesWithGroupingDistinctAndLimitOnTheOuterQuery()
    {
        // A semi-join preserves outer row multiplicity exactly, so every enclosing clause keeps
        // working. These would all be wrong if the rewrite produced an inner join.
        var queries = new[]
        {
            """
            SELECT DISTINCT c.region FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.region;
            """,
            """
            SELECT COUNT(*) FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id);
            """,
            """
            SELECT c.region, COUNT(*) FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            GROUP BY c.region ORDER BY c.region;
            """,
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.id LIMIT 1;
            """,
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(OrdersSetup, query);
            AssertRewrites(OrdersSetup, query, semiJoins: 1);
        }
    }

    [Test]
    public void MultipleEligibleConjunctsEachBecomeTheirOwnJoin()
    {
        const string query =
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
              AND NOT EXISTS (SELECT 1 FROM orders AS p WHERE p.customer_id = c.id AND p.total = 50)
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 1, antiJoins: 1);
    }

    [Test]
    public void SemiJoinLeavesTheRemainingWhereTermsInPlace()
    {
        const string query =
            """
            SELECT c.id FROM customers AS c
            WHERE c.region = 'north'
              AND EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 1);
    }

    // ------------------------------------------------------------------------------------
    // Correlated IN -> semi join.
    // ------------------------------------------------------------------------------------

    [Test]
    public void DirectCorrelatedInBecomesASemiJoin()
    {
        const string query =
            """
            SELECT c.id FROM customers AS c
            WHERE c.id IN (SELECT o.customer_id FROM orders AS o WHERE o.total = c.id * 100)
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 1);
    }

    [Test]
    public void CorrelatedInWithDuplicateInnerMatchesEmitsTheOuterRowOnce()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,7),(2,8);
            INSERT INTO i VALUES (1,7,7),(2,7,7),(3,7,7);
            """;

        const string query = "SELECT o.id FROM o WHERE o.k IN (SELECT i.v FROM i WHERE i.k = o.k) ORDER BY o.id;";

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, semiJoins: 1);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Query(connection, query).Should().HaveCount(1);
    }

    [Test]
    public void NotInIsNeverRewritten()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,7),(2,8);
            INSERT INTO i VALUES (1,7,NULL),(2,8,8);
            """;

        // `k NOT IN (…)` yields NULL when the inner set holds a NULL, which an anti-join's
        // "no matching row" answer cannot reproduce.
        const string query = "SELECT o.id FROM o WHERE o.k NOT IN (SELECT i.v FROM i WHERE i.k = o.k) ORDER BY o.id;";

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, semiJoins: 0, antiJoins: 0);
    }

    // ------------------------------------------------------------------------------------
    // Exclusions for the semi/anti rewrite.
    // ------------------------------------------------------------------------------------

    [Test]
    public void DoesNotRewriteSubqueriesUnderOr()
    {
        const string query =
            """
            SELECT c.id FROM customers AS c
            WHERE c.region = 'south' OR EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 0, antiJoins: 0);
    }

    [Test]
    public void DoesNotRewriteUncorrelatedSubqueries()
    {
        var queries = new[]
        {
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.total > 60) ORDER BY c.id;",
            "SELECT c.id FROM customers AS c WHERE c.id IN (SELECT o.customer_id FROM orders AS o WHERE o.total > 60) ORDER BY c.id;",
            "SELECT c.id FROM customers AS c WHERE NOT EXISTS (SELECT 1 FROM orders AS o WHERE o.total > 1000) ORDER BY c.id;",
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(OrdersSetup, query);
            AssertRewrites(OrdersSetup, query, semiJoins: 0, antiJoins: 0);
        }
    }

    [Test]
    public void DoesNotRewriteCardinalityAffectingOrUnsupportedSubqueryClauses()
    {
        var queries = new[]
        {
            // LIMIT / OFFSET / DISTINCT / ORDER BY inside the subquery.
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id LIMIT 0);",
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id LIMIT 1 OFFSET 2);",
            "SELECT c.id FROM customers AS c WHERE c.id IN (SELECT DISTINCT o.customer_id FROM orders AS o WHERE o.total = c.id * 100);",
            // An aggregate returns a row even for an empty input, so EXISTS is always true.
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT COUNT(*) FROM orders AS o WHERE o.customer_id = c.id);",
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id GROUP BY o.total);",
            // A joined, derived or compound inner source is outside the one-table contract.
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o JOIN customers AS x ON x.id = o.customer_id WHERE o.customer_id = c.id);
            """,
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM (SELECT customer_id FROM orders) AS o WHERE o.customer_id = c.id);",
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id
                          UNION ALL SELECT 1 FROM orders AS p WHERE p.customer_id = c.id);
            """,
            // A nested subquery inside the moved WHERE would change correlation scope.
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o
                          WHERE o.customer_id = c.id AND o.total > (SELECT MIN(total) FROM orders));
            """,
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(OrdersSetup, query);
            AssertRewrites(OrdersSetup, query, semiJoins: 0, antiJoins: 0);
        }
    }

    [Test]
    public void DoesNotRewriteCorrelationPredicatesThatAreNotPlainEqualities()
    {
        var queries = new[]
        {
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id > c.id) ORDER BY c.id;",
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id IS c.id) ORDER BY c.id;",
            "SELECT c.id FROM customers AS c WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id + c.id = 3) ORDER BY c.id;",
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id OR o.total = c.id)
            ORDER BY c.id;
            """,
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(OrdersSetup, query);
            AssertRewrites(OrdersSetup, query, semiJoins: 0, antiJoins: 0);
        }
    }

    [Test]
    public void DoesNotRewriteWhenTheOuterQueryHasAnOuterJoin()
    {
        const string query =
            """
            SELECT c.id FROM customers AS c LEFT JOIN orders AS r ON r.customer_id = c.id
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id)
            ORDER BY c.id, r.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, semiJoins: 0, antiJoins: 0);
    }

    [Test]
    public void DoesNotRewriteNonDeterministicOrFallibleSubqueryExpressions()
    {
        // A non-deterministic call in the moved WHERE would run once per (outer, inner) pair.
        AssertRewrites(
            OrdersSetup,
            """
            SELECT c.id FROM customers AS c
            WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id AND abs(random()) >= 0);
            """,
            semiJoins: 0);

        // IN scans its subquery to the end, so an operand that can raise must not be moved into
        // a loop that stops at the first match.
        AssertRewrites(
            OrdersSetup,
            "SELECT c.id FROM customers AS c WHERE c.name IN (SELECT upper(o.total) FROM orders AS o WHERE o.customer_id = c.id);",
            semiJoins: 0);
    }

    [Test]
    public void DoesNotRewriteInWhoseInnerWhereCanFailOnALaterRow()
    {
        // `1 IN (SELECT …)` runs its subquery to the end, so a malformed JSON row *after* the
        // matching one still raises. A semi-join stops at the first match and would silently
        // return the row instead (unnest.rs:291-314 rejects the same shape by checking every
        // inner WHERE term with expression_can_fail_on_input).
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER, j TEXT);
            INSERT INTO o VALUES (1,7);
            INSERT INTO i VALUES (1,7,7,'{"a":1}'),(2,7,7,'not json');
            """;

        const string query =
            "SELECT o.id FROM o WHERE o.k IN (SELECT i.v FROM i WHERE i.k = o.k AND json_extract(i.j,'$.a') IS NOT NULL);";

        AssertFailsLikeSqlite(setup, query, "malformed JSON");
        AssertRewrites(setup, query, semiJoins: 0, expectFailure: true);

        // The same subquery with well-formed JSON everywhere still declines: fallibility is a
        // property of the expression, not of the data that happens to be stored.
        const string wellFormed =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER, j TEXT);
            INSERT INTO o VALUES (1,7);
            INSERT INTO i VALUES (1,7,7,'{"a":1}'),(2,7,7,'{"a":2}');
            """;

        AssertMatchesSqlite(wellFormed, query);
        AssertRewrites(wellFormed, query, semiJoins: 0);
    }

    [Test]
    public void SemiJoinProbePreservesComparisonAffinity()
    {
        // The semi-join reuses the correlated subquery's statement-cached hash probe. That probe
        // must hash both sides under the affinity SQLite's `=` applies: INTEGER 7 and TEXT '007'
        // compare equal because the numeric operand pulls the text one to numeric. Hashing the
        // probe value under the scanned column's own TEXT affinity instead turns 7 into '7' and
        // answers "no such row".
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, n INTEGER, t TEXT);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, t TEXT, n INTEGER);
            INSERT INTO o VALUES (1,1,7,'007'),(2,2,8,'8');
            INSERT INTO i VALUES (1,1,'007',7),(2,2,'x',99);
            """;

        var queries = new[]
        {
            // Synthetic IN equality: the outer INTEGER is compared with the inner TEXT column.
            "SELECT o.id FROM o WHERE o.n IN (SELECT i.t FROM i WHERE i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE o.t IN (SELECT i.n FROM i WHERE i.k = o.k) ORDER BY o.id;",
            // The same comparison written by hand, in both operand orders.
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.t = o.n AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE o.n = i.t AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.n = o.t AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE o.t = i.n AND i.k = o.k) ORDER BY o.id;",
            // A literal and a CAST on the value side keep their own (absent / declared) affinity.
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.t = 7 AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.t = CAST(o.n AS TEXT) AND i.k = o.k) ORDER BY o.id;",
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(setup, query);
            AssertRewrites(setup, query, semiJoins: 1);
        }

        // A BLOB-declared column has *no* affinity for comparison purposes, so a TEXT operand
        // pulls it to text: the INTEGER 7 stored in `i.b` equals the TEXT '7' in `o.t`.
        const string blobSetup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, t TEXT);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, b BLOB);
            INSERT INTO o VALUES (1,1,'7'),(2,2,'zz');
            INSERT INTO i VALUES (1,1,7),(2,2,42);
            """;

        var blobQueries = new[]
        {
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.b = o.t AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE o.t = i.b AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE o.t IN (SELECT i.b FROM i WHERE i.k = o.k) ORDER BY o.id;",
        };

        foreach (var query in blobQueries)
        {
            AssertMatchesSqlite(blobSetup, query);
            AssertRewrites(blobSetup, query, semiJoins: 1);
        }

        // The unnested form must still find the row, not just agree with SQLite by declining.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        ReadScalar(connection, "SELECT COUNT(*) FROM o WHERE o.n IN (SELECT i.t FROM i WHERE i.k = o.k);")
            .Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void SemiJoinProbePreservesComparisonCollation()
    {
        // SQLite picks the comparison's collating sequence by operand order: an explicit COLLATE
        // first, then the *left* operand's declared collation, then the right one's. The probe
        // used to hash on whichever side happened to be the scanned column, so `o.c = i.c` with
        // a NOCASE outer column and a BINARY inner one lost every case-insensitive match.
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, c TEXT COLLATE NOCASE, p TEXT, r TEXT COLLATE RTRIM);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, c TEXT, p TEXT COLLATE NOCASE, r TEXT);
            INSERT INTO o VALUES (1,1,'ABC','ABC','ab  '),(2,2,'zz','zz','zz');
            INSERT INTO i VALUES (1,1,'abc','abc','ab'),(2,2,'qq','qq','qq');
            """;

        var queries = new[]
        {
            // NOCASE comes from the outer column, which is the IN operator's left operand.
            "SELECT o.id FROM o WHERE o.c IN (SELECT i.c FROM i WHERE i.k = o.k) ORDER BY o.id;",
            // Written by hand: the left operand decides, so these two disagree with each other
            // and both must agree with SQLite.
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE o.c = i.c AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.c = o.c AND i.k = o.k) ORDER BY o.id;",
            // Mirrored declarations: now the inner column is the NOCASE one.
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.p = o.p AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE o.p = i.p AND i.k = o.k) ORDER BY o.id;",
            // An explicit COLLATE outranks both declarations, on either side.
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.c = o.c COLLATE NOCASE AND i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.c COLLATE BINARY = o.c AND i.k = o.k) ORDER BY o.id;",
            // RTRIM ignores trailing spaces on the outer side only.
            "SELECT o.id FROM o WHERE o.r IN (SELECT i.r FROM i WHERE i.k = o.k) ORDER BY o.id;",
            "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.r = o.r AND i.k = o.k) ORDER BY o.id;",
        };

        foreach (var query in queries)
        {
            AssertMatchesSqlite(setup, query);
            AssertRewrites(setup, query, semiJoins: 1);
        }

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        ReadScalar(connection, "SELECT COUNT(*) FROM o WHERE o.c IN (SELECT i.c FROM i WHERE i.k = o.k);")
            .Should().Be(SqlValue.Integer(1));
        ReadScalar(connection, "SELECT COUNT(*) FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.c = o.c AND i.k = o.k);")
            .Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void DoesNotRewriteWhenAnotherWhereConjunctCanFail()
    {
        // `AND` stops at the first false, so the WHERE clause is also an error guard. A join
        // runs before every remaining WHERE term and for every outer row, so moving the
        // subquery out either invents an error the guard suppressed or suppresses one the
        // original raised.
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, j TEXT);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER);
            INSERT INTO o VALUES (1,1,'{"a":1}'),(2,2,'oops');
            INSERT INTO i VALUES (1,1);
            """;

        // The surviving conjunct raises on row 2 in the original; a semi-join would filter that
        // row out first and hide the error.
        const string hidden =
            "SELECT o.id FROM o WHERE json_extract(o.j,'$.a') = 1 AND EXISTS (SELECT 1 FROM i WHERE i.k = o.k);";
        AssertFailsLikeSqlite(setup, hidden, "malformed JSON");
        AssertRewrites(setup, hidden, semiJoins: 0, expectFailure: true);

        // The mirror image: the guard rejects row 2 before the subquery's fallible correlation
        // ever runs, so the rewrite must not evaluate it for every outer row.
        const string guarded =
            "SELECT o.id FROM o WHERE o.k = 1 AND EXISTS (SELECT 1 FROM i WHERE i.k = json_extract(o.j,'$.a'));";
        AssertMatchesSqlite(setup, guarded);
        AssertRewrites(setup, guarded, semiJoins: 0);

        // Same hazard through IN and through NOT EXISTS.
        const string inGuarded =
            "SELECT o.id FROM o WHERE json_extract(o.j,'$.a') = 1 AND o.k IN (SELECT i.k FROM i WHERE i.k = o.k);";
        AssertFailsLikeSqlite(setup, inGuarded, "malformed JSON");
        AssertRewrites(setup, inGuarded, semiJoins: 0, expectFailure: true);

        const string antiGuarded =
            "SELECT o.id FROM o WHERE json_extract(o.j,'$.a') = 1 AND NOT EXISTS (SELECT 1 FROM i WHERE i.k = o.k);";
        AssertFailsLikeSqlite(setup, antiGuarded, "malformed JSON");
        AssertRewrites(setup, antiGuarded, antiJoins: 0, expectFailure: true);

        // A single conjunct has nothing to short-circuit against, so it still rewrites even
        // though the subquery reads JSON.
        const string alone = "SELECT o.id FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.k = o.k) ORDER BY o.id;";
        AssertMatchesSqlite(setup, alone);
        AssertRewrites(setup, alone, semiJoins: 1);

        // And a multi-conjunct WHERE whose terms cannot raise keeps rewriting: the guard is
        // about fallibility, not about arity.
        const string safe =
            "SELECT o.id FROM o WHERE o.k > 0 AND EXISTS (SELECT 1 FROM i WHERE i.k = o.k) ORDER BY o.id;";
        AssertMatchesSqlite(setup, safe);
        AssertRewrites(setup, safe, semiJoins: 1);
    }

    [Test]
    public void AntiJoinDeclinesTermsThatDoNotTouchTheInnerTable()
    {
        // `c.region = 'north'` never reads `o`, so moving it into the join would reject rows
        // that NOT EXISTS keeps.
        const string query =
            """
            SELECT c.id FROM customers AS c
            WHERE NOT EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id AND c.region = 'north')
            ORDER BY c.id;
            """;

        AssertMatchesSqlite(OrdersSetup, query);
        AssertRewrites(OrdersSetup, query, antiJoins: 0);
    }

    [Test]
    public void RewriteIsSkippedInsideTriggerBodies()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, OrdersSetup);
        Execute(
            connection,
            """
            CREATE TABLE audit(id INTEGER);
            CREATE TRIGGER t AFTER INSERT ON customers BEGIN
                INSERT INTO audit
                SELECT c.id FROM customers AS c
                WHERE EXISTS (SELECT 1 FROM orders AS o WHERE o.customer_id = c.id) AND c.id = NEW.id;
            END;
            """);

        Execute(connection, "INSERT INTO customers VALUES (5,'eve','east');");
        ReadScalar(connection, "SELECT COUNT(*) FROM audit;").Should().Be(SqlValue.Integer(0));

        // Trigger bodies skip prepare-time schema validation, so the rewrite must stay off there.
        database.RewriteDiagnostics.SemiJoins.Should().Be(0);
        database.RewriteDiagnostics.AntiJoins.Should().Be(0);
    }

    [Test]
    public void SemiJoinScansTheInnerTableOncePerStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER);");
        const int rows = 900;
        Execute(connection, "BEGIN;");
        for (var index = 1; index <= rows; index++)
        {
            Execute(connection, $"INSERT INTO o VALUES ({index}, {index});");
            Execute(connection, $"INSERT INTO i VALUES ({index}, {index});");
        }

        Execute(connection, "COMMIT;");

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var matched = ReadScalar(
            connection,
            "SELECT COUNT(*) FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.k = o.k);");
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        matched.Should().Be(SqlValue.Integer(rows));
        database.RewriteDiagnostics.SemiJoins.Should().Be(1);
        elapsed.TotalSeconds.Should().BeLessThan(
            30.0,
            $"the semi-join took {elapsed.TotalSeconds:F1}s; the inner table must be materialized once "
            + "for the statement instead of being re-planned for every outer row");
    }

    [Test]
    public void SemiJoinKeepsTheHashProbeInsteadOfDegradingToANestedScan()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER);");
        const int rows = 4000;
        Execute(connection, "BEGIN;");
        for (var batch = 1; batch <= rows; batch += 200)
        {
            var values = string.Join(
                ',',
                Enumerable.Range(batch, Math.Min(200, rows - batch + 1)).Select(index => $"({index},{index})"));
            Execute(connection, $"INSERT INTO o VALUES {values};");
            Execute(connection, $"INSERT INTO i VALUES {values};");
        }

        Execute(connection, "COMMIT;");

        // The correlated equality feeds the same statement-cached transient hash index the
        // un-rewritten subquery used, so the join stays roughly linear. A nested scan would be
        // 16M condition evaluations here and would blow well past the bound.
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var semi = ReadScalar(connection, "SELECT COUNT(*) FROM o WHERE EXISTS (SELECT 1 FROM i WHERE i.k = o.k);");
        var anti = ReadScalar(connection, "SELECT COUNT(*) FROM o WHERE NOT EXISTS (SELECT 1 FROM i WHERE i.k = o.k);");
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        semi.Should().Be(SqlValue.Integer(rows));
        anti.Should().Be(SqlValue.Integer(0));
        database.RewriteDiagnostics.SemiJoins.Should().Be(1);
        database.RewriteDiagnostics.AntiJoins.Should().Be(1);
        elapsed.TotalSeconds.Should().BeLessThan(
            20.0,
            $"the semi/anti joins took {elapsed.TotalSeconds:F1}s at {rows} x {rows} rows; the rewrite must "
            + "not trade the correlated subquery's hash probe for a nested scan");
    }

    // ------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------

    private static void AssertRewrites(
        string setup,
        string query,
        long flattened = -1,
        long semiJoins = -1,
        long antiJoins = -1,
        bool expectFailure = false)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        database.ResetRewriteDiagnostics();
        try
        {
            Query(connection, query);
            expectFailure.Should().BeFalse($"of query {query}, which was expected to raise");
        }
        catch (EmbeddedSqlException) when (expectFailure)
        {
            // The counters below still describe the route the failing statement took.
        }

        var diagnostics = database.RewriteDiagnostics;
        if (flattened >= 0)
            diagnostics.FlattenedFromSubqueries.Should().Be(flattened, $"of query {query}");
        if (semiJoins >= 0)
            diagnostics.SemiJoins.Should().Be(semiJoins, $"of query {query}");
        if (antiJoins >= 0)
            diagnostics.AntiJoins.Should().Be(antiJoins, $"of query {query}");
    }

    /// <summary>
    /// Asserts that both engines reject the statement, and with the same diagnostic. A rewrite
    /// that silently answered where SQLite raises would otherwise look like a passing test.
    /// </summary>
    private static void AssertFailsLikeSqlite(string setup, string query, string expectedMessagePart)
    {
        var managed = Record(() => RunManaged(setup, query));
        var sqlite = Record(() => RunSqlite(setup, query));

        sqlite.Should().NotBeNull($"of query {query}, which SQLite is expected to reject");
        sqlite!.Message.Should().Contain(expectedMessagePart, $"of query {query}");
        managed.Should().NotBeNull($"of query {query}, which the managed engine must reject too");
        managed!.Message.Should().Contain(expectedMessagePart, $"of query {query}");
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertMatchesSqlite(string setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Columns.Should().Equal(sqlite.Columns, $"of query {query}");
        managed.Rows.Should().HaveCount(sqlite.Rows.Count, $"of query {query}");
        for (var index = 0; index < sqlite.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index], $"of row {index} of query {query}");
    }

    private static QueryOutput RunManaged(string setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        var rows = new List<string[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(index => Normalize(statement.GetValue(index)))
                .ToArray());
        }

        return new QueryOutput(columns, rows);
    }

    private static QueryOutput RunSqlite(string setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = setup;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => Normalize(reader.GetValue(index)))
                .ToArray());
        }

        return new QueryOutput(columns, rows);
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetColumnName).ToArray();
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Single()[0];

    private static IReadOnlyList<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(statement.GetValue)
                .ToArray());
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static string Normalize(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "N:",
            SqlValueKind.Integer => $"I:{value.AsInteger()}",
            SqlValueKind.Real => $"R:{value.AsReal():R}",
            SqlValueKind.Text => $"T:{value.AsText()}",
            SqlValueKind.Blob => $"B:{Convert.ToBase64String(value.AsBlob().Span)}",
            _ => throw new InvalidOperationException($"Unknown managed value kind {value.Kind}."),
        };

    private static string Normalize(object value)
        => value switch
        {
            DBNull => "N:",
            long integer => $"I:{integer}",
            double real => $"R:{real:R}",
            string text => $"T:{text}",
            byte[] blob => $"B:{Convert.ToBase64String(blob)}",
            _ => throw new InvalidOperationException($"Unknown SQLite value type {value.GetType().Name}."),
        };

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<string[]> Rows);
}
