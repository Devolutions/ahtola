using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Coverage for the correlated single-value aggregate decorrelation of
/// <c>EmbeddedDatabase.SubqueryRewrites.cs</c>: the group-first route (a <c>GROUP BY</c> derived
/// table LEFT JOINed on the correlation keys) and the join-first route (the inner table LEFT
/// JOINed, grouped by the outer rowid, with the comparison moved to <c>HAVING</c> behind an
/// aggregate <c>FILTER</c>).
/// <para>
/// Every correctness assertion is differential against Microsoft.Data.Sqlite, and the counters on
/// <see cref="EmbeddedDatabase.RewriteDiagnostics"/> pin which route ran. An "eligible" test
/// asserts the route fired; an "excluded" test asserts it declined <em>and</em> that the decline
/// was counted, so a gate that stops being reached at all is caught rather than passing silently.
/// </para>
/// </summary>
public sealed class AggregateSubqueryDecorrelationTests
{
    private const string Setup =
        """
        CREATE TABLE outer_rows(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
        CREATE TABLE inner_rows(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
        INSERT INTO outer_rows VALUES
            (1,1,10),
            (2,1,200),
            (3,2,5),
            (4,NULL,1),
            (5,9,1),
            (6,2,4);
        INSERT INTO inner_rows VALUES
            (1,1,100),
            (2,1,300),
            (3,2,7),
            (4,NULL,4),
            (5,2,NULL);
        """;

    // ------------------------------------------------------------------------------------
    // Group-first: eligible shapes.
    // ------------------------------------------------------------------------------------

    [TestCase("avg(i.v)")]
    [TestCase("count(i.v)")]
    [TestCase("count(*)")]
    [TestCase("total(i.v)")]
    [TestCase("min(i.v)")]
    [TestCase("max(i.v)")]
    public void GroupFirstDecorrelatesTheSafeAggregatesInAComparison(string aggregate)
    {
        var query =
            $"""
            SELECT o.id, o.v
            FROM outer_rows o
            WHERE o.v < (SELECT {aggregate} FROM inner_rows i WHERE i.k = o.k)
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    [TestCase("avg(i.v)")]
    [TestCase("count(i.v)")]
    [TestCase("count(*)")]
    [TestCase("total(i.v)")]
    [TestCase("min(i.v)")]
    [TestCase("max(i.v)")]
    public void GroupFirstDecorrelatesTheSafeAggregatesInTheSelectList(string aggregate)
    {
        var query =
            $"""
            SELECT o.id, (SELECT {aggregate} FROM inner_rows i WHERE i.k = o.k) AS agg
            FROM outer_rows o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// SQLite names an unaliased result column after the source text of the expression that
    /// produced it. Substituting the subquery with a joined column must not rename it.
    /// </summary>
    [Test]
    public void GroupFirstKeepsTheResultColumnNameOfAnUnaliasedSubquery()
    {
        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k)
            FROM outer_rows o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 1, joinFirst: 0, declines: 0);

        // count() and total() go through COALESCE, which replaces the projection expression
        // wholesale — the published name still has to be the original source text.
        const string counted =
            """
            SELECT o.id, (SELECT count(*) FROM inner_rows i WHERE i.k = o.k)
            FROM outer_rows o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, counted);
        AssertRewrites(Setup, counted, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// Several correlated aggregate subqueries in one statement each get their own grouped
    /// table, and the synthetic alias of the second must not collide with the first.
    /// </summary>
    [Test]
    public void GroupFirstDecorrelatesSeveralSubqueriesInOneStatement()
    {
        const string query =
            """
            SELECT o.id,
                   (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k) AS a,
                   (SELECT max(i.v) FROM inner_rows i WHERE i.k = o.id) AS b
            FROM outer_rows o
            WHERE o.v < (SELECT count(*) FROM inner_rows i WHERE i.k = o.k) * 1000
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 3, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// The whole point of the empty-input analysis: <c>count</c> answers integer 0 and
    /// <c>total</c> real 0.0 for a key with no matching inner row, while the null-producing
    /// aggregates answer NULL. A left join produces NULL for all of them, so the two zero cases
    /// must come back through COALESCE — including the exact storage class, which the
    /// differential comparison distinguishes.
    /// </summary>
    [Test]
    public void GroupFirstKeepsTheEmptyInputValueOfEveryAggregate()
    {
        const string query =
            """
            SELECT o.id,
                   (SELECT count(*) FROM inner_rows i WHERE i.k = o.k) AS c,
                   (SELECT count(i.v) FROM inner_rows i WHERE i.k = o.k) AS cv,
                   (SELECT total(i.v) FROM inner_rows i WHERE i.k = o.k) AS t,
                   (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k) AS a,
                   (SELECT min(i.v) FROM inner_rows i WHERE i.k = o.k) AS mn,
                   (SELECT max(i.v) FROM inner_rows i WHERE i.k = o.k) AS mx
            FROM outer_rows o
            ORDER BY o.id;
            """;

        // Row 5 has key 9, which no inner row carries, and row 4 has a NULL key that matches
        // nothing. Both exercise the empty-input path.
        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 6, joinFirst: 0, declines: 0);

        // Typed spot check so a REAL 0.0 answered as INTEGER 0 cannot slip through.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, Setup);
        Query(connection, "SELECT (SELECT total(i.v) FROM inner_rows i WHERE i.k = o.k) FROM outer_rows o WHERE o.id = 5;")
            .Single()[0].Kind.Should().Be(SqlValueKind.Real);
        Query(connection, "SELECT (SELECT count(*) FROM inner_rows i WHERE i.k = o.k) FROM outer_rows o WHERE o.id = 5;")
            .Single()[0].Kind.Should().Be(SqlValueKind.Integer);
    }

    /// <summary>
    /// Several outer rows asking for the same key must each get their own copy of the one
    /// grouped row, and none of them may be duplicated by the join.
    /// </summary>
    [Test]
    public void GroupFirstHandlesDuplicateAndMissingOuterKeys()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,7),(2,7),(3,7),(4,8),(5,99);
            INSERT INTO i VALUES (1,7,1),(2,7,2),(3,7,3),(4,8,10);
            """;

        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM i WHERE i.k = o.k) AS a
            FROM o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 1, joinFirst: 0, declines: 0);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Query(connection, query).Should().HaveCount(5, "the join must not multiply outer rows");
    }

    /// <summary>
    /// A NULL correlation key matches nothing on either side of the rewrite: the subquery's
    /// <c>i.k = o.k</c> is NULL for every inner row, and the rewritten join condition is NULL for
    /// every grouped row — including the group the inner NULL keys form.
    /// </summary>
    [Test]
    public void GroupFirstNeverMatchesANullCorrelationKey()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,NULL),(2,1);
            INSERT INTO i VALUES (1,NULL,5),(2,NULL,6),(3,1,9);
            """;

        const string query =
            """
            SELECT o.id,
                   (SELECT count(*) FROM i WHERE i.k = o.k) AS c,
                   (SELECT avg(i.v) FROM i WHERE i.k = o.k) AS a
            FROM o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 2, joinFirst: 0, declines: 0);
    }

    [Test]
    public void GroupFirstSupportsSeveralCorrelationColumns()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,1,1),(2,1,2),(3,2,1),(4,9,9);
            INSERT INTO i VALUES (1,1,1,10),(2,1,1,20),(3,1,2,30),(4,2,1,40);
            """;

        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM i WHERE i.a = o.a AND i.b = o.b) AS a
            FROM o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// A WHERE term that only reads the inner table stays inside the grouped derived table, so
    /// the group it forms is exactly the set the subquery would have aggregated.
    /// </summary>
    [Test]
    public void GroupFirstKeepsInnerOnlyFiltersInsideTheGroupedTable()
    {
        const string query =
            """
            SELECT o.id, (SELECT count(*) FROM inner_rows i WHERE i.k = o.k AND i.v > 50) AS c
            FROM outer_rows o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// The correlation may be written either way round, and the aggregate may sit inside a larger
    /// NULL-propagating expression whose empty-input value is still known.
    /// </summary>
    [Test]
    public void GroupFirstAcceptsReversedCorrelationAndAggregateExpressions()
    {
        const string reversed =
            """
            SELECT o.id, (SELECT avg(i.v) FROM inner_rows i WHERE o.k = i.k) AS a
            FROM outer_rows o
            ORDER BY o.id;
            """;
        AssertMatchesSqlite(Setup, reversed);
        AssertRewrites(Setup, reversed, groupFirst: 1, joinFirst: 0, declines: 0);

        const string expression =
            """
            SELECT o.id, (SELECT avg(i.v) * 2 + 1 FROM inner_rows i WHERE i.k = o.k) AS a
            FROM outer_rows o
            ORDER BY o.id;
            """;
        AssertMatchesSqlite(Setup, expression);
        AssertRewrites(Setup, expression, groupFirst: 1, joinFirst: 0, declines: 0);

        const string casted =
            """
            SELECT o.id, (SELECT CAST(max(i.v) AS TEXT) FROM inner_rows i WHERE i.k = o.k) AS a
            FROM outer_rows o
            ORDER BY o.id;
            """;
        AssertMatchesSqlite(Setup, casted);
        AssertRewrites(Setup, casted, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// <c>count(*) + 0</c> is 0 for an empty input, not NULL, so its empty-input value is not
    /// known to the NULL-propagation analysis and the rewrite must decline rather than answer
    /// NULL where SQLite answers 0.
    /// </summary>
    [Test]
    public void DeclinesAnExpressionWhoseEmptyInputValueIsNotKnown()
    {
        const string query =
            """
            SELECT o.id, (SELECT count(*) + 0 FROM inner_rows i WHERE i.k = o.k) AS c
            FROM outer_rows o
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    [Test]
    public void GroupFirstComposesWithOuterGroupingAndAggregates()
    {
        const string query =
            """
            SELECT o.k, count(*) AS n
            FROM outer_rows o
            WHERE o.v < (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k)
            GROUP BY o.k
            ORDER BY o.k;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    [Test]
    public void GroupFirstSupportsAnAliasedOuterStarProjection()
    {
        const string query =
            """
            SELECT o.*
            FROM outer_rows o
            WHERE o.v < (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k)
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// A bare <c>*</c> publishes every FROM column, so the grouped table would leak two synthetic
    /// columns into the result. Expanding the star here would mean reimplementing SQLite's
    /// result-column naming, so the rewrite declines.
    /// </summary>
    [Test]
    public void DeclinesABareOuterStarProjection()
    {
        const string query =
            """
            SELECT *
            FROM outer_rows o
            WHERE o.v < (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k)
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    // ------------------------------------------------------------------------------------
    // Join-first: aggregates that must not run for an unused key.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// <c>sum</c> can overflow, so group-first is not allowed to compute it for a key no outer
    /// row asks for. Join-first reaches only the keys the outer rows ask for.
    /// </summary>
    [Test]
    public void JoinFirstDecorrelatesSum()
    {
        const string query =
            """
            SELECT o.id, o.v
            FROM outer_rows o
            WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k);
            """;

        AssertMatchesSqliteUnordered(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 1, declines: 0);
    }

    /// <summary>
    /// The exact regression the <c>FILTER (WHERE i.rowid IS NOT NULL)</c> guard exists for: an
    /// outer row with no matching inner row is null-padded by the left join, and an unguarded
    /// <c>count(*)</c> would count that invented row as 1 where the subquery answers 0.
    /// </summary>
    [Test]
    public void JoinFirstDoesNotCountTheNullPaddedRow()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER, j TEXT);
            INSERT INTO o VALUES (1,1,5),(2,9,5),(3,NULL,5);
            INSERT INTO i VALUES (1,1,1,'{"a":1}');
            """;

        // json_extract makes the aggregate input fallible, which forces the join-first route
        // even for count().
        const string query =
            """
            SELECT o.id
            FROM o
            WHERE o.v > (SELECT count(json_extract(i.j, '$.a')) FROM i WHERE i.k = o.k);
            """;

        AssertMatchesSqliteUnordered(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 1, declines: 0);

        const string sumQuery =
            """
            SELECT o.id
            FROM o
            WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k);
            """;

        // sum() over no rows is NULL, and `5 > NULL` is NULL, so rows 2 and 3 must be dropped.
        AssertMatchesSqliteUnordered(setup, sumQuery);
        AssertRewrites(setup, sumQuery, groupFirst: 0, joinFirst: 1, declines: 0);
    }

    [Test]
    public void JoinFirstKeepsRemainingWhereTermsAndInnerOnlyFilters()
    {
        const string query =
            """
            SELECT o.id, o.k
            FROM outer_rows o
            WHERE o.id <> 3 AND o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k AND i.v < 200);
            """;

        AssertMatchesSqliteUnordered(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 1, declines: 0);
    }

    /// <summary>
    /// An outer row that matches several inner rows appears once per match after the join, so the
    /// grouping by the outer rowid has to collapse them back into exactly one output row.
    /// </summary>
    [Test]
    public void JoinFirstEmitsOneRowPerOuterRow()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,7,100),(2,7,100);
            INSERT INTO i VALUES (1,7,1),(2,7,2),(3,7,3),(4,7,4);
            """;

        const string query = "SELECT o.id FROM o WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k);";

        AssertMatchesSqliteUnordered(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 1, declines: 0);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        Query(connection, query).Should().HaveCount(2);
    }

    [TestCase("<")]
    [TestCase("<=")]
    [TestCase(">")]
    [TestCase(">=")]
    [TestCase("=")]
    [TestCase("<>")]
    public void JoinFirstMovesEveryComparisonOperatorToHaving(string @operator)
    {
        var query =
            $"""
            SELECT o.id
            FROM outer_rows o
            WHERE o.v {@operator} (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k);
            """;

        AssertMatchesSqliteUnordered(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 1, declines: 0);
    }

    [Test]
    public void JoinFirstAcceptsTheSubqueryOnEitherSideOfTheComparison()
    {
        const string query =
            """
            SELECT o.id
            FROM outer_rows o
            WHERE (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) < o.v;
            """;

        AssertMatchesSqliteUnordered(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 1, declines: 0);
    }

    /// <summary>
    /// Join-first only knows how to move one whole WHERE comparison. Every other placement of the
    /// value — a select-list reference, an operand of a larger expression, a second WHERE term —
    /// would leave a use of a subquery the rewrite is about to delete.
    /// </summary>
    [TestCase("SELECT o.id, (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) AS s FROM outer_rows o ORDER BY o.id;")]
    [TestCase("SELECT o.id FROM outer_rows o WHERE o.v > 1 + (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) ORDER BY o.id;")]
    [TestCase("SELECT o.id FROM outer_rows o WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) OR o.id = 1 ORDER BY o.id;")]
    [TestCase("SELECT o.id FROM outer_rows o WHERE (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) IS NULL ORDER BY o.id;")]
    public void DeclinesJoinFirstWhenTheValueIsNotOneWholeComparison(string query)
    {
        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// Join-first cannot preserve these outer clauses around the grouping step it introduces, so
    /// each of them declines the rewrite and the subquery keeps its per-row evaluation.
    /// </summary>
    [TestCase("SELECT DISTINCT o.k FROM outer_rows o WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k);")]
    [TestCase("SELECT o.id FROM outer_rows o WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) ORDER BY o.id;")]
    [TestCase("SELECT o.id FROM outer_rows o WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) LIMIT 2;")]
    [TestCase("SELECT count(*) FROM outer_rows o WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k);")]
    [TestCase("SELECT o.k FROM outer_rows o WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) GROUP BY o.k;")]
    public void DeclinesJoinFirstForOuterClausesItCannotPreserve(string query)
    {
        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// After the join an outer row appears once per matching inner row, so a surviving WHERE term
    /// runs once per copy. A nondeterministic term could then keep some copies and drop others,
    /// leaving the aggregate to see only part of the row's inner rows.
    /// </summary>
    [Test]
    public void DeclinesJoinFirstWhenAnotherWhereTermIsNondeterministic()
    {
        const string query =
            """
            SELECT o.id
            FROM outer_rows o
            WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k) AND random() <> 0;
            """;

        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    [Test]
    public void DeclinesJoinFirstWhenAnotherWhereTermReadsACorrelatedSubquery()
    {
        const string query =
            """
            SELECT o.id
            FROM outer_rows o
            WHERE o.v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k)
              AND EXISTS (SELECT 1 FROM inner_rows x WHERE x.k = o.k AND x.v > 1000);
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// Join-first moves a real table into the enclosing scope, so a name that resolved to
    /// exactly one column before the rewrite must still resolve to exactly one after it. An
    /// unqualified enclosing reference that the inner table also answers to, and an unqualified
    /// reference inside the subquery that the outer table also answers to, both decline.
    /// </summary>
    [Test]
    public void DeclinesJoinFirstWhenAddingTheInnerTableWouldMakeANameAmbiguous()
    {
        // `id` and `v` exist on both tables, so the unqualified outer references would become
        // ambiguous once inner_rows joins the enclosing FROM clause.
        const string unqualifiedOuter =
            """
            SELECT id FROM outer_rows o WHERE v > (SELECT sum(i.v) FROM inner_rows i WHERE i.k = o.k);
            """;
        AssertMatchesSqlite(Setup, unqualifiedOuter);
        AssertRewrites(Setup, unqualifiedOuter, groupFirst: 0, joinFirst: 0, declines: 1);

        // The mirror image: `sum(v)` and `k` resolve to the subquery's only table today, but
        // would have two candidates in the joined scope.
        const string unqualifiedInner =
            """
            SELECT o.id FROM outer_rows o WHERE o.v > (SELECT sum(v) FROM inner_rows i WHERE k = o.k);
            """;
        AssertMatchesSqlite(Setup, unqualifiedInner);
        AssertRewrites(Setup, unqualifiedInner, groupFirst: 0, joinFirst: 0, declines: 1);

        // An unqualified reference whose name the inner table does not carry is unaffected.
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, budget INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, amount INTEGER);
            INSERT INTO o VALUES (1,1,500),(2,1,5),(3,9,5);
            INSERT INTO i VALUES (1,1,10),(2,1,20);
            """;
        const string safe = "SELECT budget FROM o WHERE budget > (SELECT sum(i.amount) FROM i WHERE i.k = o.k);";
        AssertMatchesSqliteUnordered(setup, safe);
        AssertRewrites(setup, safe, groupFirst: 0, joinFirst: 1, declines: 0);
    }

    [Test]
    public void DeclinesJoinFirstWhenANestedQueryCouldResolveAgainstTheMovedTable()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE z(q INTEGER, w INTEGER);
            INSERT INTO o VALUES (1,1,5),(2,9,5);
            INSERT INTO i VALUES (1,1,1),(2,1,2);
            INSERT INTO z VALUES (1,7),(9,7);
            """;
        var queries = new[]
        {
            """
            SELECT o.id FROM o
            WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k)
              AND (SELECT z.w FROM z WHERE z.q = k) > 0;
            """,
            """
            SELECT (SELECT z.w FROM z WHERE z.q = k) AS w FROM o
            WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k);
            """,
            """
            SELECT o.id FROM o
            WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k)
              AND EXISTS (SELECT 1 FROM z WHERE z.q = k);
            """,
        };

        foreach (var query in queries)
        {
            AssertMatchesSqliteUnordered(setup, query);
            AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
        }
    }

    [Test]
    public void DeclinesJoinFirstWhenTheMovedQualifierWouldCaptureAnOuterScope()
    {
        const string setup =
            """
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER, z INTEGER);
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO i VALUES (1,1,10,1),(2,1,20,0);
            INSERT INTO o VALUES (1,1,25);
            """;
        const string query =
            """
            SELECT i.id,
                   (SELECT o.id FROM o
                    WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k)
                      AND i.z = 1)
            FROM i
            ORDER BY i.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 2);

        const string delete =
            """
            DELETE FROM i
            WHERE i.id IN (SELECT o.id FROM o
                           WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k)
                             AND i.z = 1);
            """;
        AssertMatchesSqlite(
            setup + Environment.NewLine + delete,
            "SELECT id, k, v, z FROM i ORDER BY id;");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        database.ResetRewriteDiagnostics();
        Execute(connection, delete);
        database.RewriteDiagnostics.AggregateGroupFirstRewrites.Should().Be(0);
        database.RewriteDiagnostics.AggregateJoinFirstRewrites.Should().Be(0);
        database.RewriteDiagnostics.AggregateDecorrelationDeclines.Should().Be(2);
    }

    /// <summary>
    /// Join-first needs a rowid on both sides: <c>GROUP BY o.rowid</c> makes one group per outer
    /// row, and <c>i.rowid IS NOT NULL</c> is how the NULL-padded row is recognized.
    /// </summary>
    [Test]
    public void DeclinesJoinFirstForWithoutRowidTables()
    {
        const string setup =
            """
            CREATE TABLE o(k INTEGER, v INTEGER, PRIMARY KEY(k, v)) WITHOUT ROWID;
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            INSERT INTO o VALUES (1,5),(9,5);
            INSERT INTO i VALUES (1,1,1),(2,1,2);
            """;

        const string query = "SELECT o.k FROM o WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k);";

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 1);

        const string innerSetup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(k INTEGER, v INTEGER, PRIMARY KEY(k, v)) WITHOUT ROWID;
            INSERT INTO o VALUES (1,1,5),(2,9,5);
            INSERT INTO i VALUES (1,1),(1,2);
            """;

        const string innerQuery = "SELECT o.id FROM o WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k);";
        AssertMatchesSqlite(innerSetup, innerQuery);
        AssertRewrites(innerSetup, innerQuery, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// A declared <c>rowid</c> column shadows the pseudo-column, so <c>o.rowid</c> would name an
    /// ordinary value that need not be unique and need not be non-NULL.
    /// </summary>
    [Test]
    public void DeclinesJoinFirstWhenARowidSpellingIsShadowed()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER, rowid TEXT);
            INSERT INTO o VALUES (1,1,5),(2,9,5);
            INSERT INTO i VALUES (1,1,1,NULL),(2,1,2,NULL);
            """;

        const string query = "SELECT o.id FROM o WHERE o.v > (SELECT sum(i.v) FROM i WHERE i.k = o.k);";

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    // ------------------------------------------------------------------------------------
    // Unused-key safety: which route an expression forces.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Group-first evaluates the aggregate for every key, including keys no outer row asks for.
    /// A fallible input would then raise an error the original statement never raises, so those
    /// shapes must take the join-first route — and when join-first does not apply, no route.
    /// </summary>
    [Test]
    public void AFallibleAggregateInputForcesJoinFirstAndNeverInventsAnError()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, j TEXT);
            INSERT INTO o VALUES (1,1,5);
            INSERT INTO i VALUES (1,1,'{"a":1}'),(2,99,'not json at all');
            """;

        // Key 99 holds malformed JSON, and no outer row asks for it. The original statement
        // therefore never parses it, and neither may the rewrite.
        const string query =
            """
            SELECT o.id
            FROM o
            WHERE o.v > (SELECT avg(json_extract(i.j, '$.a')) FROM i WHERE i.k = o.k);
            """;

        AssertMatchesSqliteUnordered(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 1, declines: 0);

        // Same fallible input in the select list: join-first cannot move a select-list value, so
        // the subquery stays as it is rather than taking the unsafe group-first route.
        const string projected =
            """
            SELECT o.id, (SELECT avg(json_extract(i.j, '$.a')) FROM i WHERE i.k = o.k) AS a
            FROM o
            ORDER BY o.id;
            """;
        AssertMatchesSqlite(setup, projected);
        AssertRewrites(setup, projected, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// A fallible inner WHERE term cannot take either route. Group-first would evaluate it against
    /// unused keys, while join-first would move it into an ON condition whose key-first evaluation
    /// could hide an error from a non-matching row.
    /// </summary>
    [Test]
    public void AFallibleInnerWhereTermDeclinesBothRoutes()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER, j TEXT);
            INSERT INTO o VALUES (1,1,500);
            INSERT INTO i VALUES (1,1,7,'{"a":1}'),(2,99,8,'nope');
            """;

        const string query =
            """
            SELECT o.id
            FROM o
            WHERE o.v > (SELECT count(*) FROM i WHERE i.k = o.k AND json_extract(i.j, '$.a') = 1);
            """;

        AssertMatchesSqliteUnordered(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 1);

        // With the fallible term first, SQLite evaluates it for the non-matching malformed row
        // before the correlation equality can short-circuit. The declined rewrite must preserve
        // that runtime error instead of silently returning the matching outer row.
        const string fallibleFirst =
            """
            SELECT o.id
            FROM o
            WHERE o.v > (SELECT count(*) FROM i WHERE json_extract(i.j, '$.a') = 1 AND i.k = o.k);
            """;

        AssertFailsLikeSqlite(setup, fallibleFirst, "malformed JSON");
    }

    /// <summary>
    /// The string aggregates can outgrow the largest SQL value for an unused key, and an
    /// extension aggregate has no known empty-input value at all. Neither may take group-first;
    /// <c>group_concat</c> in a comparison can still take join-first, but a registered aggregate
    /// declines outright.
    /// </summary>
    [Test]
    public void StringAndExtensionAggregatesDoNotTakeGroupFirst()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v TEXT);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v TEXT);
            INSERT INTO o VALUES (1,1,'zzz'),(2,9,'a');
            INSERT INTO i VALUES (1,1,'b'),(2,1,'c');
            """;

        const string concat =
            """
            SELECT o.id FROM o WHERE o.v > (SELECT group_concat(i.v) FROM i WHERE i.k = o.k);
            """;
        AssertMatchesSqliteUnordered(setup, concat);
        AssertRewrites(setup, concat, groupFirst: 0, joinFirst: 1, declines: 0);

        const string projected =
            """
            SELECT o.id, (SELECT group_concat(i.v) FROM i WHERE i.k = o.k) AS g FROM o ORDER BY o.id;
            """;
        AssertMatchesSqlite(setup, projected);
        AssertRewrites(setup, projected, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    // ------------------------------------------------------------------------------------
    // Comparison compatibility between the grouping key and the join key.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// An inner BINARY key puts <c>'A'</c> and <c>'a'</c> in two groups, while a NOCASE outer key
    /// joins to both — the outer row would come back twice. The rewrite must decline whenever the
    /// two keys do not compare identically.
    /// </summary>
    [Test]
    public void DeclinesWhenTheCorrelationKeysUseDifferentCollations()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k TEXT COLLATE NOCASE);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k TEXT, v INTEGER);
            INSERT INTO o VALUES (1,'a');
            INSERT INTO i VALUES (1,'A',10),(2,'a',20);
            """;

        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM i WHERE i.k = o.k) AS a FROM o ORDER BY o.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// A BLOB-affinity inner key stores <c>1</c> and <c>'1'</c> as two groups, while a numeric
    /// outer key converts the text and joins to both.
    /// </summary>
    [Test]
    public void DeclinesWhenTheCorrelationKeysUseDifferentAffinities()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k BLOB, v INTEGER);
            INSERT INTO o VALUES (1,1);
            INSERT INTO i VALUES (1,1,10),(2,'1',20);
            """;

        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM i WHERE i.k = o.k) AS a FROM o ORDER BY o.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// Matching declared collations are fine: the grouping and the join then partition the key
    /// space the same way, so at most one grouped row can match an outer row.
    /// </summary>
    [Test]
    public void AcceptsMatchingCollationsAndAffinities()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k TEXT COLLATE NOCASE);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k TEXT COLLATE NOCASE, v INTEGER);
            INSERT INTO o VALUES (1,'a'),(2,'B');
            INSERT INTO i VALUES (1,'A',10),(2,'a',20),(3,'b',30);
            """;

        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM i WHERE i.k = o.k) AS a FROM o ORDER BY o.id;
            """;

        AssertMatchesSqlite(setup, query);
        AssertRewrites(setup, query, groupFirst: 1, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// A registered collation is ordinary managed code, and group-first would run it while
    /// forming groups for keys no outer row asks for. Only the built-in sequences are safe there.
    /// </summary>
    [Test]
    public void DeclinesGroupFirstForAKeyWithAnApplicationRegisteredCollation()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k TEXT COLLATE REVERSED);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k TEXT COLLATE REVERSED, v INTEGER);
            INSERT INTO o VALUES (1,'a');
            INSERT INTO i VALUES (1,'a',10),(2,'b',20);
            """;

        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM i WHERE i.k = o.k) AS a FROM o ORDER BY o.id;
            """;

        using var database = new EmbeddedDatabase();
        database.RegisterCollation("REVERSED", static (left, right) => string.CompareOrdinal(right, left));
        using var connection = database.Connect();
        Execute(connection, setup);
        database.ResetRewriteDiagnostics();
        Query(connection, query);

        var diagnostics = database.RewriteDiagnostics;
        diagnostics.AggregateGroupFirstRewrites.Should().Be(0);
        diagnostics.AggregateJoinFirstRewrites.Should().Be(0);
        diagnostics.AggregateDecorrelationDeclines.Should().Be(1);
    }

    // ------------------------------------------------------------------------------------
    // Shapes that are never eligible.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// An uncorrelated aggregate subquery already evaluates once for the whole statement, so
    /// there is nothing to decorrelate and no candidate to count.
    /// </summary>
    [Test]
    public void DoesNotTouchUncorrelatedAggregateSubqueries()
    {
        const string query =
            """
            SELECT o.id FROM outer_rows o WHERE o.v < (SELECT avg(i.v) FROM inner_rows i) ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// A scalar subquery with no aggregate is not a candidate at all: its value depends on which
    /// row the subquery happens to return, which no grouping reproduces.
    /// </summary>
    [Test]
    public void DoesNotTouchNonAggregateScalarSubqueries()
    {
        const string query =
            """
            SELECT o.id, (SELECT i.v FROM inner_rows i WHERE i.k = o.k) AS v FROM outer_rows o ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 0);
    }

    /// <summary>
    /// Each of these inner clauses changes which rows the aggregate sees or how many results the
    /// subquery yields, and neither rewrite preserves it around the grouping it introduces.
    /// </summary>
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k GROUP BY i.v")]
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k LIMIT 1")]
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k ORDER BY i.v")]
    [TestCase("SELECT avg(DISTINCT i.v) FROM inner_rows i WHERE i.k = o.k LIMIT 1 OFFSET 0")]
    public void DeclinesInnerClausesThatChangeTheAggregatedRows(string inner)
    {
        var query = $"SELECT o.id, ({inner}) AS a FROM outer_rows o ORDER BY o.id;";

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// A column read outside an aggregate makes the value depend on which row of the group the
    /// engine keeps, and a nested subquery keeps a correlation scope neither rewrite moves.
    /// </summary>
    [TestCase("SELECT avg(i.v) + i.id FROM inner_rows i WHERE i.k = o.k")]
    [TestCase("SELECT avg(i.v) + (SELECT 1) FROM inner_rows i WHERE i.k = o.k")]
    [TestCase("SELECT avg(i.v + random()) FROM inner_rows i WHERE i.k = o.k")]
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k AND random() > 0")]
    public void DeclinesInnerExpressionsItCannotModel(string inner)
    {
        var query = $"SELECT o.id, ({inner}) AS a FROM outer_rows o ORDER BY o.id;";

        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// The correlation has to be a plain equality between one inner and one outer column. A
    /// range or expression correlation cannot become a grouping key.
    /// </summary>
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k > o.k")]
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k + 1")]
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k + 0 = o.k")]
    [TestCase("SELECT avg(i.v) FROM inner_rows i WHERE i.k IS o.k")]
    public void DeclinesCorrelationsThatAreNotPlainColumnEqualities(string inner)
    {
        var query = $"SELECT o.id, ({inner}) AS a FROM outer_rows o ORDER BY o.id;";

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// The subquery must read one ordinary base table: a view, CTE or joined inner source cannot
    /// be grouped in place or moved into the enclosing query.
    /// </summary>
    [Test]
    public void DeclinesInnerSourcesThatAreNotOneBaseTable()
    {
        const string setup = Setup + "\nCREATE VIEW iv AS SELECT * FROM inner_rows;";

        const string view =
            "SELECT o.id, (SELECT avg(x.v) FROM iv x WHERE x.k = o.k) AS a FROM outer_rows o ORDER BY o.id;";
        AssertMatchesSqlite(setup, view);
        AssertRewrites(setup, view, groupFirst: 0, joinFirst: 0, declines: 1);

        const string joined =
            """
            SELECT o.id,
                   (SELECT avg(a.v) FROM inner_rows a, inner_rows b WHERE a.k = o.k AND b.id = a.id) AS a
            FROM outer_rows o
            ORDER BY o.id;
            """;
        AssertMatchesSqlite(Setup, joined);
        AssertRewrites(Setup, joined, groupFirst: 0, joinFirst: 0, declines: 1);

        const string cte =
            """
            WITH c AS (SELECT * FROM inner_rows)
            SELECT o.id, (SELECT avg(x.v) FROM c x WHERE x.k = o.k) AS a
            FROM outer_rows o
            ORDER BY o.id;
            """;
        AssertMatchesSqlite(Setup, cte);
        AssertRewrites(Setup, cte, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// A correlation that reaches two levels out belongs to a scope neither rewrite can add a
    /// join to, so the middle SELECT must decline.
    /// </summary>
    [Test]
    public void DeclinesACorrelationToAGrandparentScope()
    {
        const string query =
            """
            SELECT g.id,
                   (SELECT (SELECT avg(i.v) FROM inner_rows i WHERE i.k = g.k) FROM outer_rows m WHERE m.id = g.id) AS a
            FROM outer_rows g
            ORDER BY g.id;
            """;

        AssertMatchesSqlite(Setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, Setup);
        database.ResetRewriteDiagnostics();
        Query(connection, query);
        database.RewriteDiagnostics.AggregateGroupFirstRewrites.Should().Be(0);
        database.RewriteDiagnostics.AggregateJoinFirstRewrites.Should().Be(0);
    }

    /// <summary>
    /// A pre-existing outer join already decides which rows are NULL-padded; adding another one
    /// in front of that decision would change the padding.
    /// </summary>
    [Test]
    public void DeclinesWhenTheOuterQueryAlreadyHasAnOuterJoin()
    {
        const string query =
            """
            SELECT o.id, (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.k) AS a
            FROM outer_rows o LEFT JOIN inner_rows p ON p.id = o.id
            ORDER BY o.id;
            """;

        AssertMatchesSqlite(Setup, query);
        AssertRewrites(Setup, query, groupFirst: 0, joinFirst: 0, declines: 1);
    }

    /// <summary>
    /// The rewrite runs after every prepare-time validation, so a statement the original engine
    /// rejects must still be rejected with the same diagnostic.
    /// </summary>
    [Test]
    public void PreservesPrepareTimeDiagnostics()
    {
        AssertFailsLikeSqlite(
            Setup,
            "SELECT o.id, (SELECT avg(i.nope) FROM inner_rows i WHERE i.k = o.k) FROM outer_rows o;",
            "no such column");
        AssertFailsLikeSqlite(
            Setup,
            "SELECT o.id, (SELECT avg(i.v) FROM inner_rows i WHERE i.k = o.missing) FROM outer_rows o;",
            "no such column");
        AssertFailsLikeSqlite(
            Setup,
            "SELECT o.id, (SELECT avg(i.v) FROM no_such_table i WHERE i.k = o.k) FROM outer_rows o;",
            "no such table");
    }

    /// <summary>
    /// A runtime error the original statement raises must survive the rewrite: the group-first
    /// form still evaluates the same fallible expression over the same rows.
    /// </summary>
    [Test]
    public void PreservesRuntimeErrorsTheOriginalStatementRaises()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, j TEXT);
            INSERT INTO o VALUES (1,1,5);
            INSERT INTO i VALUES (1,1,'not json');
            """;

        AssertFailsLikeSqlite(
            setup,
            "SELECT o.id FROM o WHERE o.v > (SELECT avg(json_extract(i.j, '$.a')) FROM i WHERE i.k = o.k);",
            "malformed JSON");
    }

    /// <summary>
    /// Trigger bodies skip <c>ValidateQuerySchema</c>, so no rewrite may run there.
    /// </summary>
    [Test]
    public void DoesNotRewriteInsideTriggerBodies()
    {
        const string setup =
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE log(n REAL);
            INSERT INTO i VALUES (1,1,10),(2,1,20);
            CREATE TRIGGER t AFTER INSERT ON o BEGIN
                INSERT INTO log
                SELECT (SELECT avg(x.v) FROM i x WHERE x.k = c.k) FROM o c WHERE c.id = NEW.id;
            END;
            """;

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        database.ResetRewriteDiagnostics();
        Execute(connection, "INSERT INTO o VALUES (1,1,5);");

        database.RewriteDiagnostics.AggregateGroupFirstRewrites.Should().Be(0);
        database.RewriteDiagnostics.AggregateJoinFirstRewrites.Should().Be(0);
        Query(connection, "SELECT n FROM log;").Single()[0].AsReal().Should().Be(15.0);
    }

    /// <summary>
    /// Decorrelation is a plan-shape change, not a result change: whichever route runs, the
    /// answer has to be the one the per-row subquery produced.
    /// </summary>
    [Test]
    public void GroupFirstDoesNotDegradeToAPerRowScan()
    {
        const int rows = 400;
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            """
            CREATE TABLE o(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            CREATE TABLE i(id INTEGER PRIMARY KEY, k INTEGER, v INTEGER);
            """);

        Execute(connection, "BEGIN;");
        for (var index = 1; index <= rows; index++)
        {
            Execute(
                connection,
                $"INSERT INTO o VALUES ({index}, {index}, {index}); INSERT INTO i VALUES ({index}, {index}, {index * 2});");
        }

        Execute(connection, "COMMIT;");

        database.ResetRewriteDiagnostics();
        var started = DateTime.UtcNow;
        var result = Query(connection, "SELECT count(*) FROM o WHERE o.v < (SELECT avg(i.v) FROM i WHERE i.k = o.k);");
        var elapsed = DateTime.UtcNow - started;

        database.RewriteDiagnostics.AggregateGroupFirstRewrites.Should().Be(1);
        result.Single()[0].AsInteger().Should().Be(rows);
        elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(20),
            $"the grouped rewrite took {elapsed.TotalSeconds:F1}s at {rows} x {rows} rows");
    }

    // ------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------

    private static void AssertRewrites(
        string setup,
        string query,
        long groupFirst = -1,
        long joinFirst = -1,
        long declines = -1)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        database.ResetRewriteDiagnostics();
        Query(connection, query);

        var diagnostics = database.RewriteDiagnostics;
        if (groupFirst >= 0)
            diagnostics.AggregateGroupFirstRewrites.Should().Be(groupFirst, $"of query {query}");
        if (joinFirst >= 0)
            diagnostics.AggregateJoinFirstRewrites.Should().Be(joinFirst, $"of query {query}");
        if (declines >= 0)
            diagnostics.AggregateDecorrelationDeclines.Should().Be(declines, $"of query {query}");
    }

    private static void AssertFailsLikeSqlite(string setup, string query, string expectedMessagePart)
    {
        var managed = Record(() => RunManaged(setup, query));
        var sqlite = Record(() => RunSqlite(setup, query));

        sqlite.Should().NotBeNull($"of query {query}, which SQLite is expected to reject");
        sqlite!.Message.Should().ContainEquivalentOf(expectedMessagePart, $"of query {query}");
        managed.Should().NotBeNull($"of query {query}, which the managed engine must reject too");
        managed!.Message.Should().ContainEquivalentOf(expectedMessagePart, $"of query {query}");
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

    /// <summary>
    /// The join-first route declines an outer ORDER BY, so its queries have no defined row order.
    /// Compare the two result multisets instead, which still catches a dropped, duplicated or
    /// altered row.
    /// </summary>
    private static void AssertMatchesSqliteUnordered(string setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Columns.Should().Equal(sqlite.Columns, $"of query {query}");
        Flatten(managed).Should().Equal(Flatten(sqlite), $"of query {query}");

        static IEnumerable<string> Flatten(QueryOutput output)
            => output.Rows.Select(row => string.Join('\u001f', row)).Order(StringComparer.Ordinal);
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
