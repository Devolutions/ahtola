using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Coverage for outer-group aggregate ownership: a subquery of a GROUP BY query may reference an
// aggregate whose argument columns belong to the OUTER block. Native SQLite hoists such
// aggregates into the outer query's aggregate list, so they (a) remain legal when the
// immediately enclosing SELECT is aggregate-bearing (projection/HAVING/ORDER BY), (b) evaluate
// against the outer group's rows, and (c) do NOT by themselves make the subquery produce
// aggregate cardinality — a subquery whose only aggregates are outer-owned behaves as a plain
// query (zero rows over an empty selection, so the scalar subquery yields NULL). Misplaced
// outer aggregates (WHERE of a non-aggregate-bearing subquery, or a single aggregate call mixing
// inner and outer columns) are native misuse errors; the managed engine rejects them through the
// scalar-arity path instead, which is an accepted message-level divergence.
//
// Every expectation below was probed against native sqlite3 3.53.3 on the same seed data:
//   o(x,e):  (1,1),(2,1),(3,2),(4,2)     o0(y,e): (10,12),(20,24),(30,6)
public sealed class OuterAggregateOwnershipTests
{
    private const string Seed = """
        CREATE TABLE o(x INT, e INT); INSERT INTO o VALUES (1,1),(2,1),(3,2),(4,2);
        CREATE TABLE o0(y INT, e INT); INSERT INTO o0 VALUES (10,12),(20,24),(30,6);
        """;

    [Test]
    public void CorrelatedScalarSubqueryWhereReferencingOuterGroupAggregateEvaluatesPerGroup()
    {
        // EF Core GroupBy_with_aggregate_containing_complex_where shape: the subquery's WHERE
        // references the OUTER group's MAX(o.x). The reference hoists to the outer aggregate
        // list, so it evaluates per group (2 then 4) and drives the inner correlation.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, (SELECT MAX(o0.y) FROM o0 WHERE o0.e = MAX(o.x)*6) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(10),
                SqlValue.Integer(2), SqlValue.Integer(20));
    }

    [Test]
    public void SubqueryWhoseOnlyAggregatesAreOuterOwnedBehavesAsPlainQuery()
    {
        // Native: MAX(o.x) is hoisted OUT of the subquery, leaving it non-aggregate, so the
        // empty correlation match (o0.e is never 1 or 2) yields zero rows -> scalar NULL.
        // A regression here kept aggregate cardinality and produced one redirected row (2/4).
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, (SELECT MAX(o.x) FROM o0 WHERE o0.e = o.e) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Null,
                SqlValue.Integer(2), SqlValue.Null);
    }

    [Test]
    public void MixedInnerAndOuterOwnedAggregatesKeepAggregateCardinality()
    {
        // MAX(o0.y) is inner-owned, so the subquery stays an aggregate: over the empty match it
        // yields one row (NULL -> -1), while the hoisted MAX(o.x) evaluates on the group (2/4).
        // Native: 1|-98, 2|-96.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, (SELECT COALESCE(MAX(o0.y), -1) * 100 + MAX(o.x) FROM o0 WHERE o0.e = o.e) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(-98),
                SqlValue.Integer(2), SqlValue.Integer(-96));
    }

    [Test]
    public void CountStarKeepsSubqueryAggregateWhenOuterAggregateIsHoisted()
    {
        // COUNT(*) is column-free, so it is always inner-owned and keeps the subquery aggregate
        // even though MAX(o.x) hoists. Over the empty match COUNT(*) is 0; the hoisted MAX
        // contributes 2/4. Native: 1|2, 2|4. Guards the refinement's column-free filter.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, (SELECT COUNT(*) * 1000 + COALESCE(MAX(o.x), -1) FROM o0 WHERE o0.e = o.e) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(2),
                SqlValue.Integer(2), SqlValue.Integer(4));
    }

    [Test]
    public void ExistsOverInnerAggregateWithEmptySourceYieldsTrue()
    {
        // EXISTS parity: an aggregate-bearing subquery produces exactly one row even when its
        // source is empty (WHERE 0), so EXISTS is true for every group. Native: 1|1, 2|1.
        // Guards the "no scope claims ownership -> inner-owned over the empty set" fall-through.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, EXISTS(SELECT MAX(o0.y) FROM o0 WHERE 0) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(1),
                SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void ExistsWithOwnAggregateAndOuterAggregateInWhereIsLegal()
    {
        // The subquery bears its own aggregate (MAX(o0.y) in the projection), which licenses the
        // outer-owned MAX(o.x) in its WHERE. Native: 1|1, 2|1 (both correlations match a row).
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, EXISTS(SELECT MAX(o0.y) FROM o0 WHERE o0.e = MAX(o.x)*6) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(1),
                SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void InnerGroupByHavingReferencingOuterAggregateEvaluatesPerGroup()
    {
        // The inner query is aggregate-bearing via its own GROUP BY; the HAVING reference to the
        // outer group's MAX(o.x) hoists and filters groups per outer group. Native: 1|10, 2|20.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, (SELECT MAX(o0.y) FROM o0 GROUP BY o0.e HAVING o0.e = MAX(o.x)*6) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(10),
                SqlValue.Integer(2), SqlValue.Integer(20));
    }

    [Test]
    public void UnqualifiedOuterColumnInHoistedAggregateResolvesAgainstTheGroup()
    {
        // Unqualified x does not exist on o0, so it binds to the outer o.x; MAX(x) hoists.
        // o0.e > 2 and o0.e > 4 both match all three o0 rows. Native: 1|30, 2|30.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        ReadRows(
                connection,
                "SELECT o.e, (SELECT MAX(o0.y) FROM o0 WHERE o0.e > MAX(x)) FROM o GROUP BY o.e;")
            .Should()
            .Equal(
                SqlValue.Integer(1), SqlValue.Integer(30),
                SqlValue.Integer(2), SqlValue.Integer(30));
    }

    [Test]
    public void OuterAggregateInWhereOfNonAggregateSubqueryIsRejected()
    {
        // Native: "misuse of aggregate function MAX()" (the subquery bears no aggregate, so the
        // outer-owned reference is illegal). The managed engine rejects the same shape through
        // the scalar-arity path; only the error level is contractual, the message diverges.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        var act = () => ReadRows(
            connection,
            "SELECT o.e, (SELECT 42 FROM o0 WHERE o0.e = MAX(o.x)*6 LIMIT 1) FROM o GROUP BY o.e;");

        act.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void OuterAggregateInExistsWhereWithoutOwnAggregateIsRejected()
    {
        // Same misuse shape through EXISTS: the WHERE references the outer aggregate but the
        // subquery bears no aggregate of its own. Native: "misuse of aggregate function MAX()".
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        var act = () => ReadRows(
            connection,
            "SELECT o.e, EXISTS(SELECT 42 FROM o0 WHERE o0.e = MAX(o.x)*6) FROM o GROUP BY o.e;");

        act.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void SingleAggregateCallMixingInnerAndOuterColumnsIsRejected()
    {
        // MAX(o0.y - 100 + o.x) mixes inner and outer columns in one call. Native: "misuse of
        // aggregate: MAX()". Managed rejects through the scalar-arity path as well.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        ExecuteBatched(connection, Seed);

        var act = () => ReadRows(
            connection,
            "SELECT o.e, (SELECT MAX(o0.y) FROM o0 WHERE o0.e > MAX(o0.y - 100 + o.x)) FROM o GROUP BY o.e;");

        act.Should().Throw<EmbeddedSqlException>();
    }

    // Row-major flattened values so AwesomeAssertions compares SqlValue element-wise.
    private static List<SqlValue> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
        {
            for (var i = 0; i < statement.GetColumnCount(); i++)
            {
                values.Add(statement.GetValue(i));
            }
        }
        return values;
    }

    private static void ExecuteBatched(EmbeddedConnection connection, string sql)
    {
        foreach (var statementText in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var statement = connection.Prepare(statementText);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        }
    }
}
