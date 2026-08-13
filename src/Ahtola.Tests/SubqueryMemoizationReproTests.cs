using System.Diagnostics;
using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Coverage for ENGINE #8-variant: an uncorrelated scalar subquery in a DELETE's WHERE must be
// evaluated once per statement, not once per candidate row (EF Core NorthwindBulkUpdates stalls).
// Memoization applies only when the subquery is uncorrelated (proven by evaluating it with no
// outer row) and deterministic over stable table data; correlated, non-deterministic, and
// user-function subqueries deliberately stay on the per-row path.
public sealed class SubqueryMemoizationReproTests
{
    [Test]
    public void UserFunctionsInSubqueriesAreNeverMemoized()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE d(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY, x INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1, 7);");
        const int rowCount = 200;
        for (var i = 1; i <= rowCount; i++)
            Execute(connection, $"INSERT INTO d VALUES ({i}, {i});");

        var bumpCount = 0;
        connection.RegisterScalarFunction(
            "bump",
            1,
            args =>
            {
                bumpCount++;
                return args[0];
            });

        Execute(connection, "DELETE FROM d WHERE k = (SELECT bump(x) FROM a WHERE a.id = 1);");

        // A user-registered function is opaque to the determinism classifier, so the subquery
        // must keep the per-row behavior: memoizing it would pin a stale result. The counter
        // proves the subquery still runs once per candidate row.
        bumpCount.Should().Be(rowCount, "a user function is opaque, so its subquery is never memoized");
    }

    [Test]
    public void UncorrelatedScalarSubqueryInDeleteWhereIsEvaluatedOncePerStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE d(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, x INTEGER);");
        const int rowCount = 2500;
        for (var i = 1; i <= rowCount; i++)
        {
            Execute(connection, $"INSERT INTO d VALUES ({i}, {i});");
            Execute(connection, $"INSERT INTO big VALUES ({i}, {i});");
        }

        var start = Stopwatch.GetTimestamp();
        Execute(connection, "DELETE FROM d WHERE k = (SELECT COUNT(*) FROM big);");
        var elapsed = Stopwatch.GetElapsedTime(start);

        // COUNT(*) is deterministic and uncorrelated, so it is computed once. Without
        // memoization the DELETE re-scans `big` for every candidate row (~6M row visits);
        // the generous bound separates the two decisively.
        elapsed.TotalSeconds.Should().BeLessThan(
            15.0,
            $"evaluating the DELETE took {elapsed.TotalSeconds:F1}s; " +
            "an uncorrelated scalar subquery must run once per statement, not per candidate row");
    }

    [Test]
    public void NestedUncorrelatedScalarSubqueryInDeleteWhereIsEvaluatedOncePerStatement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE d(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE big(id INTEGER PRIMARY KEY, j INTEGER, x INTEGER);");
        Execute(connection, "CREATE TABLE big2(id INTEGER PRIMARY KEY, y INTEGER);");
        const int outerRows = 500;
        const int innerRows = 1000;
        for (var i = 1; i <= innerRows; i++)
        {
            Execute(connection, $"INSERT INTO big VALUES ({i}, {i}, {i});");
            Execute(connection, $"INSERT INTO big2 VALUES ({i}, {i});");
        }
        for (var i = 1; i <= outerRows; i++)
            Execute(connection, $"INSERT INTO d VALUES ({i}, {i});");

        var start = Stopwatch.GetTimestamp();
        Execute(
            connection,
            "DELETE FROM d WHERE k = (SELECT MAX(x) FROM big WHERE big.j = (SELECT MIN(y) FROM big2));");
        var elapsed = Stopwatch.GetElapsedTime(start);

        // The exact EF Core BulkUpdates shape: an outer uncorrelated subquery whose WHERE holds
        // a nested uncorrelated scalar subquery. Both memoize (the nested one during the outer's
        // single evaluation), collapsing the per-row x per-row product.
        elapsed.TotalSeconds.Should().BeLessThan(
            15.0,
            $"evaluating the DELETE took {elapsed.TotalSeconds:F1}s; " +
            "nested uncorrelated subqueries must not multiply the per-row re-evaluation cost");
    }

    [Test]
    public void MemoizedScalarSubqueryDeletesCorrectRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE d(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY, x INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1, 3), (2, 9), (3, 5);");
        for (var i = 1; i <= 6; i++)
            Execute(connection, $"INSERT INTO d VALUES ({i}, {i});");

        // MAX(x) = 9 matches no k in 1..6, so nothing is deleted.
        Execute(connection, "DELETE FROM d WHERE k = (SELECT MAX(x) FROM a);");
        ReadScalar(connection, "SELECT COUNT(*) FROM d;").Should().Be(SqlValue.Integer(6));

        // MIN(x) = 3 deletes exactly the k = 3 row; the memoized result must drive the same
        // DELETE decisions as a per-row evaluation would.
        Execute(connection, "DELETE FROM d WHERE k = (SELECT MIN(x) FROM a);");
        ReadScalar(connection, "SELECT COUNT(*) FROM d;").Should().Be(SqlValue.Integer(5));
        ReadScalar(connection, "SELECT COUNT(*) FROM d WHERE k = 3;").Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void CorrelatedScalarSubqueryEvaluatesPerRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE d(id INTEGER PRIMARY KEY, k INTEGER);");
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY, x INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (2, 2), (4, 4);");
        for (var i = 1; i <= 6; i++)
            Execute(connection, $"INSERT INTO d VALUES ({i}, {i});");

        // Correlated on d.id: the no-outer-row probe cannot resolve d.id, so the subquery keeps
        // per-row evaluation. Rows 2 and 4 match (a.x = k) and are deleted; the rest see an
        // empty (NULL) subquery result and survive.
        Execute(connection, "DELETE FROM d WHERE k = (SELECT x FROM a WHERE a.id = d.id);");

        ReadScalar(connection, "SELECT COUNT(*) FROM d;").Should().Be(SqlValue.Integer(4));
        ReadScalar(connection, "SELECT COUNT(*) FROM d WHERE id IN (2, 4);").Should().Be(SqlValue.Integer(0));
        ReadScalar(connection, "SELECT COUNT(*) FROM d WHERE id IN (1, 3, 5, 6);").Should().Be(SqlValue.Integer(4));
    }

    [Test]
    public void NonDeterministicSubqueryReevaluatesPerRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE s(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, r INTEGER);");
        const int rowCount = 100;
        for (var i = 1; i <= rowCount; i++)
            Execute(connection, $"INSERT INTO s VALUES ({i});");

        // random() is non-deterministic, so the subquery must not be memoized: each row draws a
        // fresh value. If it were (incorrectly) cached, every row would share one value and the
        // distinct count would collapse to 1.
        Execute(connection, "INSERT INTO t SELECT id, (SELECT abs(random()) % 1000000) FROM s;");

        ReadScalar(connection, "SELECT COUNT(DISTINCT r) FROM t;")
            .AsInteger()
            .Should()
            .BeGreaterThan(1, "random() must be re-drawn per row, not memoized");
    }

    [Test]
    public void CorrelatedScalarInsideGroupByProjectionMemoizesTheOuterUncorrelatedSubquery()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE orders (OrderID INTEGER, CustomerID TEXT);");
        Execute(connection, "CREATE TABLE details (OrderID INTEGER, ProductID INTEGER);");

        // 30 customers x 5 orders, so GROUP BY CustomerID HAVING COUNT(*) > 2 yields 30 groups.
        for (var c = 1; c <= 30; c++)
        {
            for (var k = 1; k <= 5; k++)
            {
                Execute(connection, $"INSERT INTO orders VALUES ({c * 100 + k}, 'cust{c}');");
            }
        }
        const int detailRows = 2000;
        for (var i = 1; i <= detailRows; i++)
        {
            Execute(connection, $"INSERT INTO details VALUES ({i}, {i});");
        }

        var start = Stopwatch.GetTimestamp();
        // The EF Core BulkUpdates #8b shape: a correlated scalar subquery (on the intermediate
        // scope o0) inside the projection of a GROUP BY query, where the whole outer subquery is
        // uncorrelated with respect to the DELETE row o. The TEXT comparison also exercises the
        // collation-resolution path the sibling stack showed burning.
        Execute(
            connection,
            @"DELETE FROM details AS o WHERE o.OrderID < (
                SELECT (
                    SELECT o1.OrderID FROM orders AS o1
                    WHERE o0.CustomerID = o1.CustomerID OR (o0.CustomerID IS NULL AND o1.CustomerID IS NULL)
                    LIMIT 1)
                FROM orders AS o0
                GROUP BY o0.CustomerID
                HAVING COUNT(*) > 2
                LIMIT 1);");
        var elapsed = Stopwatch.GetElapsedTime(start);

        // The outer subquery does not reference o, so it must memoize to a single evaluation;
        // the correlated inner scalar stays per-group within that one pass. Without outer
        // memoization the whole GROUP BY re-runs once per detail row (~2000x), which this bound
        // decisively rejects.
        elapsed.TotalSeconds.Should().BeLessThan(
            15.0,
            $"evaluating the DELETE took {elapsed.TotalSeconds:F1}s; " +
            "an uncorrelated outer subquery must memoize even when it contains a correlated nested scalar");
    }

    [Test]
    public void CorrelatedScalarInsideGroupByProjectionIsNotSharedAcrossGroups()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE orders (OrderID INTEGER, CustomerID TEXT);");
        Execute(connection, "INSERT INTO orders VALUES (11, 'a'), (12, 'a'), (13, 'a');");
        Execute(connection, "INSERT INTO orders VALUES (21, 'b'), (22, 'b');");
        Execute(connection, "INSERT INTO orders VALUES (31, 'c');");

        // Per-group correlated scalar: MAX(OrderID) differs per customer (13, 22, 31). The inner
        // query yields one row per group; the outer SUMs them (13 + 22 + 31 = 66). If the
        // correlation probe resolved o0 from the ambient context instead of detecting the
        // correlation, the inner scalar would be incorrectly memoized to one group's value and
        // the SUM would collapse (e.g. 13*3 = 39).
        ReadScalar(
            connection,
            @"SELECT SUM(sub) FROM (
                  SELECT (SELECT MAX(o1.OrderID) FROM orders AS o1 WHERE o1.CustomerID = o0.CustomerID) AS sub
                  FROM orders AS o0
                  GROUP BY o0.CustomerID
              );")
            .Should()
            .Be(SqlValue.Integer(66), "a correlated nested scalar must evaluate per group, not memoize across groups");
    }

    [Test]
    public void UncorrelatedGroupBySubqueryInsideCorrelatedExistsMemoizesAcrossRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE orders (OrderID INTEGER, CustomerID TEXT);");
        Execute(connection, "CREATE TABLE details (OrderID INTEGER, ProductID INTEGER);");

        for (var c = 1; c <= 30; c++)
        {
            for (var k = 1; k <= 5; k++)
            {
                Execute(connection, $"INSERT INTO orders VALUES ({c * 100 + k}, 'cust{c}');");
            }
        }
        const int detailRows = 2000;
        for (var i = 1; i <= detailRows; i++)
        {
            Execute(connection, $"INSERT INTO details VALUES ({i}, {i});");
        }

        var start = Stopwatch.GetTimestamp();
        // The EF Core BulkUpdates #8b "_2" regression shape: the uncorrelated GROUP BY subquery
        // (with its own correlated nested scalar on o2) is an IN-operand nested inside an EXISTS
        // that is itself correlated to the DELETE row o. The EXISTS runs per row, but the
        // IN-subquery references neither o nor o0/o1, so it must memoize once for the statement
        // and be reused across every per-row EXISTS evaluation.
        Execute(
            connection,
            @"DELETE FROM details AS o WHERE EXISTS (
                SELECT 1
                FROM details AS o0 INNER JOIN orders AS o1 ON o0.OrderID = o1.OrderID
                WHERE o1.OrderID IN (
                    SELECT (
                        SELECT o3.OrderID FROM orders AS o3
                        WHERE o2.CustomerID = o3.CustomerID OR (o2.CustomerID IS NULL AND o3.CustomerID IS NULL)
                        LIMIT 1)
                    FROM orders AS o2
                    GROUP BY o2.CustomerID
                    HAVING COUNT(*) > 2
                ) AND o0.OrderID = o.OrderID AND o0.ProductID = o.ProductID);");
        var elapsed = Stopwatch.GetElapsedTime(start);

        elapsed.TotalSeconds.Should().BeLessThan(
            15.0,
            $"evaluating the DELETE took {elapsed.TotalSeconds:F1}s; " +
            "an uncorrelated subquery nested inside a correlated EXISTS must memoize once per statement");
    }

    [Test]
    public void BareGroupByCorrelatedScalarQueryAtNorthwindScaleCompletesFast()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE orders (OrderID INTEGER, CustomerID TEXT);");

        // Northwind Orders scale: 830 orders across 89 customers. Skew the distribution so a
        // realistic set of customers clears HAVING COUNT(*) > 11 (20 heavy customers x 30 orders,
        // 69 light x ~3). Spread each customer's rows pseudo-randomly through the table so the
        // per-group correlated scalar does a real scan instead of always hitting an early match.
        var customerIds = new List<string>(capacity: 830);
        for (var c = 1; c <= 20; c++)
        {
            for (var k = 0; k < 30; k++)
            {
                customerIds.Add($"cust{c}");
            }
        }
        for (var c = 21; c <= 89; c++)
        {
            for (var k = 0; k < 3; k++)
            {
                customerIds.Add($"cust{c}");
            }
        }
        var random = new Random(12345);
        for (var i = customerIds.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (customerIds[i], customerIds[j]) = (customerIds[j], customerIds[i]);
        }
        for (var i = 0; i < customerIds.Count; i++)
        {
            Execute(connection, $"INSERT INTO orders VALUES ({i + 1}, '{customerIds[i]}');");
        }

        var start = Stopwatch.GetTimestamp();
        // The sibling's standalone #8b repro (no DELETE wrapper): a correlated scalar (on the
        // intermediate scope o0) in the projection of a GROUP BY query. This is a SINGLE
        // execution, so the per-row memoization does not apply to the inner scalar (it stays
        // per-group, correctly). If this completes fast, the EF stall is purely the DELETE's
        // per-row amplification (which memoization collapses) and the bare query is a red
        // herring; if it hangs, single-execution per-group scalar evaluation is itself unbounded
        // and needs a separate fix. The TEXT comparison exercises the collation path from the
        // sibling's stack.
        var result = ReadScalar(
            connection,
            @"SELECT (
                    SELECT o1.OrderID FROM orders AS o1
                    WHERE o0.CustomerID = o1.CustomerID OR (o0.CustomerID IS NULL AND o1.CustomerID IS NULL)
                    LIMIT 1)
                FROM orders AS o0
                GROUP BY o0.CustomerID
                HAVING COUNT(*) > 11
                LIMIT 1;");
        var elapsed = Stopwatch.GetElapsedTime(start);

        result.Should().NotBeNull("the query yields one group's scalar value");
        elapsed.TotalSeconds.Should().BeLessThan(
            30.0,
            $"evaluating the bare GROUP BY + correlated scalar query took {elapsed.TotalSeconds:F1}s; " +
            "a single execution of the Northwind-scale repro must be bounded (the EF stall was the per-row amplification)");
    }

    [Test]
    public void CorrelatedExistsInEmptyAggregateUsesNullOuterRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t6(id);");

        ReadRow(
            connection,
            "SELECT EXISTS (SELECT 1 WHERE t6.id = 1), COUNT(*) FROM t6;")
            .Should()
            .Equal(SqlValue.Integer(0), SqlValue.Integer(0));

        ReadRow(connection, "SELECT rowid, t6.rowid, COUNT(*) FROM t6;")
            .Should()
            .Equal(SqlValue.Null, SqlValue.Null, SqlValue.Integer(0));

        Execute(connection, "CREATE TABLE t14(a INTEGER);");
        Execute(connection, "CREATE TABLE t14_sub(b INTEGER);");
        Execute(connection, "INSERT INTO t14_sub VALUES (1);");

        ReadRow(
            connection,
            "SELECT EXISTS (SELECT 1 FROM t14_sub WHERE b = t14.a), COUNT(*) FROM t14;")
            .Should()
            .Equal(SqlValue.Integer(0), SqlValue.Integer(0));
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static SqlValue[] ReadRow(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetValue).ToArray();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }
}
