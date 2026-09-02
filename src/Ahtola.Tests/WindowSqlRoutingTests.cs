using System.Globalization;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Compilation;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes windowed SELECTs through real bytecode: the streaming
// streaming program (sorter + aggregate opcodes emitted by WindowProgramBuilder) for the
// supported running-prefix, exact-current-row, bounded n-preceding, FOLLOWING, and RANGE/GROUPS peer shapes, and the buffered-window program
// (OpenWindowBuffer/WindowBufferCompute/WindowBufferData emitted by BufferedWindowProgramBuilder) for
// every other frame, function family, partition and ordering shape. Routed rows stay byte-identical to
// the tree-walking evaluator (cross-checked against a real SQLite build for the partitioned case).
// EXPLAIN is the ground truth for "was this lowered to bytecode?": a routed statement dumps its opcodes,
// while every deliberate fallback shape throws on EXPLAIN because EXPLAIN only describes lowered
// programs. Fallback tests also assert the evaluator still produces the correct value or raises its
// exact error.
public class WindowSqlRoutingTests
{
    private const string RunningFrame = "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
    private const string CurrentRowFrame = "ROWS BETWEEN CURRENT ROW AND CURRENT ROW";
    private const string OnePrecedingFrame = "ROWS BETWEEN 1 PRECEDING AND CURRENT ROW";

    // ---- Routed streaming-frame values -----------------------------------------------------

    [Test]
    public void UnpartitionedRunningSumRoutesAndAccumulatesInOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);");

        var query = $"SELECT id, sum(v) OVER (ORDER BY id {RunningFrame}) AS running FROM t ORDER BY id;";

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("OpenSorter")
            .And.Contain("SorterInsert")
            .And.Contain("SorterSort")
            .And.Contain("SorterData")
            .And.Contain("AggReset")
            .And.Contain("AggStep")
            .And.Contain("AggFinalize");
        // No partition -> no partition-boundary check.
        opcodes.Should().NotContain("SameGroup");

        var rows = ReadRows(connection, query);
        rows.Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(10)),
            (SqlValue.Integer(2), SqlValue.Integer(30)),
            (SqlValue.Integer(3), SqlValue.Integer(60)),
            (SqlValue.Integer(4), SqlValue.Integer(100)));
    }

    [Test]
    public void PartitionedRunningSumRoutesWithBoundaryCheckAndMatchesSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE sales(region TEXT, amount INTEGER);",
            "INSERT INTO sales VALUES ('a', 10), ('a', 20), ('b', 100), ('b', 5), ('a', 30);",
        ];
        var query =
            $"SELECT region, amount, sum(amount) OVER (PARTITION BY region ORDER BY amount {RunningFrame}) AS running " +
            "FROM sales ORDER BY region, amount;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("OpenSorter")
            .And.Contain("SorterSort")
            .And.Contain("AggReset")
            .And.Contain("AggStep")
            .And.Contain("AggFinalize")
            .And.Contain("SameGroup");

        var rows = ReadRows(connection, query);
        rows.Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Text("a"), SqlValue.Integer(10), SqlValue.Integer(10)),
            (SqlValue.Text("a"), SqlValue.Integer(20), SqlValue.Integer(30)),
            (SqlValue.Text("a"), SqlValue.Integer(30), SqlValue.Integer(60)),
            (SqlValue.Text("b"), SqlValue.Integer(5), SqlValue.Integer(5)),
            (SqlValue.Text("b"), SqlValue.Integer(100), SqlValue.Integer(105)));

        AssertMatchesSqlite(rows, setup, query);
    }

    [Test]
    public void DefaultRangeAggregateReusesPeerFrameAcrossJoinRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE w(p TEXT, v INTEGER);");
        Execute(connection, "INSERT INTO w SELECT 'p' || (value % 3), value FROM generate_series(1, 60);");

        var rows = ReadRows(connection, """
            SELECT a.p
            FROM w AS a JOIN w AS b USING (p) JOIN w AS d USING (p)
            ORDER BY a.p, sum(1e18) OVER (ORDER BY a.p)
            LIMIT 6;
            """);

        rows.Select(row => row[0]).Should().Equal(
            SqlValue.Text("p0"),
            SqlValue.Text("p0"),
            SqlValue.Text("p0"),
            SqlValue.Text("p0"),
            SqlValue.Text("p0"),
            SqlValue.Text("p0"));
    }

    [Test]
    public void MultipleWindowFunctionsSharingOneSpecRouteThroughOneSorter()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);");

        var query =
            $"SELECT id, sum(v) OVER (ORDER BY id {RunningFrame}) AS s, " +
            $"count(*) OVER (ORDER BY id {RunningFrame}) AS c FROM t ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("AggFinalize");

        var rows = ReadRows(connection, query);
        rows.Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(10), SqlValue.Integer(1)),
            (SqlValue.Integer(2), SqlValue.Integer(30), SqlValue.Integer(2)),
            (SqlValue.Integer(3), SqlValue.Integer(60), SqlValue.Integer(3)),
            (SqlValue.Integer(4), SqlValue.Integer(100), SqlValue.Integer(4)));
    }

    [Test]
    public void DistinctWindowSpecsRetainInnerOrderWithinOuterPeers()
    {
        string[] setup =
        [
            "CREATE TABLE nc (x TEXT COLLATE NOCASE, y INTEGER);",
            "INSERT INTO nc VALUES ('a', 1), ('A', 2), ('b', 3);",
        ];
        const string query =
            "SELECT y, dense_rank() OVER (ORDER BY x), " +
            "dense_rank() OVER (ORDER BY x COLLATE BINARY) FROM nc;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var rows = ReadRows(connection, query);
        rows.Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Integer(2), SqlValue.Integer(1), SqlValue.Integer(1)),
            (SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(2)),
            (SqlValue.Integer(3), SqlValue.Integer(2), SqlValue.Integer(3)));
        AssertMatchesSqlite(rows, setup, query);
    }

    [Test]
    public void NullaryCountStarRunsAsRowNumberAndRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10, 1), (20, 1), (30, 1);");

        var query = $"SELECT id, count(*) OVER (ORDER BY id {RunningFrame}) AS rn FROM t ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("AggStep");

        ReadRows(connection, query).Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(10), SqlValue.Integer(1)),
            (SqlValue.Integer(20), SqlValue.Integer(2)),
            (SqlValue.Integer(30), SqlValue.Integer(3)));
    }

    [Test]
    public void RunningMinMaxAvgRouteWithExactTypes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 30), (2, 10), (3, 20), (4, 40);");

        var query =
            $"SELECT id, min(v) OVER (ORDER BY id {RunningFrame}) AS lo, " +
            $"max(v) OVER (ORDER BY id {RunningFrame}) AS hi, " +
            $"avg(v) OVER (ORDER BY id {RunningFrame}) AS mean FROM t ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("AggFinalize");

        var rows = ReadRows(connection, query);
        rows.Select(row => (row[1], row[2], row[3])).Should().Equal(
            (SqlValue.Integer(30), SqlValue.Integer(30), SqlValue.Real(30)),
            (SqlValue.Integer(10), SqlValue.Integer(30), SqlValue.Real(20)),
            (SqlValue.Integer(10), SqlValue.Integer(30), SqlValue.Real(20)),
            (SqlValue.Integer(10), SqlValue.Integer(40), SqlValue.Real(25)));
    }

    [Test]
    public void CurrentRowCountSumAndAvgRouteThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v);",
            "INSERT INTO t VALUES (1, NULL), (2, 5), (3, 2.5), (4, '7'), (5, 'not-a-number');",
        ];
        var query =
            $"SELECT id, count(*) OVER (ORDER BY id {CurrentRowFrame}), " +
            $"count(v) OVER (ORDER BY id {CurrentRowFrame}), " +
            $"sum(v) OVER (ORDER BY id {CurrentRowFrame}), " +
            $"avg(v) OVER (ORDER BY id {CurrentRowFrame}) FROM t ORDER BY id;";
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("AggStep").And.Contain("AggFinalize").And.Contain("AggInverse");
        opcodes.Count(static opcode => opcode == "AggInverse").Should().Be(4);
        opcodes.Should().NotContain("OpenWindowBuffer").And.NotContain("WindowBufferCompute");

        var rows = ReadRows(connection, query);
        AssertMatchesSqlite(rows, setup, query);
        rows.Select(static row => row[1].AsInteger()).Should().OnlyContain(static count => count == 1);
        rows.Select(static row => row[2].AsInteger()).Should().Equal(0, 1, 1, 1, 1);
    }

    [Test]
    public void CurrentRowMinRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 30), (2, 10), (3, 20);",
        ];
        var query = $"SELECT id, min(v) OVER (ORDER BY id {CurrentRowFrame}) FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void OnePrecedingCountSumAndAvgRouteThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(grp TEXT, id INTEGER, v);",
            "INSERT INTO t VALUES ('a', 1, NULL), ('a', 2, 5), ('a', 3, 2.5), " +
            "('a', 4, '7'), ('b', 1, 'not-a-number'), ('b', 2, 10), " +
            "('c', 1, CAST('-9223372036854775808' AS INTEGER)), ('c', 2, 0.5), ('c', 3, 1);",
        ];
        var query =
            $"SELECT grp, id, count(*) OVER (PARTITION BY grp ORDER BY id {OnePrecedingFrame}), " +
            $"count(v) OVER (PARTITION BY grp ORDER BY id {OnePrecedingFrame}), " +
            $"sum(v) OVER (PARTITION BY grp ORDER BY id {OnePrecedingFrame}), " +
            $"avg(v) OVER (PARTITION BY grp ORDER BY id {OnePrecedingFrame}) " +
            "FROM t ORDER BY grp, id;";
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Count(static opcode => opcode == "AggInverse").Should().Be(4);
        opcodes.Should().Contain("JumpIf").And.NotContain("OpenWindowBuffer");

        var rows = ReadRows(connection, query);
        AssertMatchesSqlite(rows, setup, query);
        rows.Select(static row => row[2].AsInteger()).Should().Equal(1, 2, 2, 2, 1, 2, 1, 2, 2);
        rows.Select(static row => row[3].AsInteger()).Should().Equal(0, 1, 2, 2, 1, 2, 1, 2, 2);
    }

    [Test]
    public void PartitionWithoutWindowOrderRoutesInScanOrderWithinPartition()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(grp INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (1, 20), (2, 30), (2, 40);");

        var query = $"SELECT grp, sum(v) OVER (PARTITION BY grp {RunningFrame}) AS running FROM t ORDER BY grp;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("SameGroup");

        ReadRows(connection, query).Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(10)),
            (SqlValue.Integer(1), SqlValue.Integer(30)),
            (SqlValue.Integer(2), SqlValue.Integer(30)),
            (SqlValue.Integer(2), SqlValue.Integer(70)));
    }

    [Test]
    public void UnorderedUnpartitionedRunningFrameRoutesAndPreservesScanOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (5), (3), (8), (1);");

        // No PARTITION BY, no window ORDER BY, and no top-level ORDER BY: the sorter preserves
        // scan order, so the running total accumulates in insertion order.
        var query = $"SELECT v, sum(v) OVER ({RunningFrame}) AS running FROM t;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("AggFinalize");

        ReadRows(connection, query).Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(5), SqlValue.Integer(5)),
            (SqlValue.Integer(3), SqlValue.Integer(8)),
            (SqlValue.Integer(8), SqlValue.Integer(16)),
            (SqlValue.Integer(1), SqlValue.Integer(17)));
    }

    [Test]
    public void BareRowsUnboundedPrecedingIsTreatedAsRunningFrameAndRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        // "ROWS UNBOUNDED PRECEDING" (no BETWEEN) parses to UNBOUNDED PRECEDING .. CURRENT ROW.
        var query = "SELECT id, sum(v) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS running FROM t ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("SorterSort").And.Contain("AggStep");

        ReadRows(connection, query).Select(row => row[1]).Should().Equal(
            SqlValue.Integer(10), SqlValue.Integer(30), SqlValue.Integer(60));
    }

    [Test]
    public void WhereFilteredRunningWindowRoutesWithFilterOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);");

        // WHERE runs before windowing, so the running total only folds the surviving rows.
        var query = $"SELECT id, sum(v) OVER (ORDER BY id {RunningFrame}) AS running FROM t WHERE v >= 20 ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("Filter").And.Contain("AggFinalize");

        ReadRows(connection, query).Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(2), SqlValue.Integer(20)),
            (SqlValue.Integer(3), SqlValue.Integer(50)),
            (SqlValue.Integer(4), SqlValue.Integer(90)));
    }

    [Test]
    public void RoutedWindowSelectUsesAliasThenExpressionTextForColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");

        ColumnNames(connection, $"SELECT id, sum(v) OVER (ORDER BY id {RunningFrame}) AS running FROM t;")
            .Should().Equal("id", "running");
        // SQLite labels an unaliased window call with the verbatim expression text.
        ColumnNames(connection, $"SELECT id, sum(v) OVER (ORDER BY id {RunningFrame}) FROM t;")
            .Should().Equal("id", $"sum(v) OVER (ORDER BY id {RunningFrame})");
    }

    // ---- Buffered-window routing (shapes the streaming builder cannot model) -----------------

    [Test]
    public void DefaultRangeFrameRoutesThroughTheStreamingPeerProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        var query = "SELECT id, sum(v) OVER (ORDER BY id) AS running FROM t ORDER BY id;";
        ReadRows(connection, query).Select(row => row[1]).Should().Equal(
            SqlValue.Integer(10), SqlValue.Integer(30), SqlValue.Integer(60));

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("OpenSorter").And.Contain("OpenEphemeral").And.Contain("EphemeralInsert");
        opcodes.Should().NotContain("OpenWindowBuffer").And.NotContain("WindowBufferCompute");
    }

    [Test]
    public void DefaultRangeTiedPeersShareTheRunningTotalAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(grp INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (1, 20), (2, 30);",
        ];
        const string query =
            "SELECT grp, sum(v) OVER (ORDER BY grp) AS running FROM t ORDER BY grp;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");

        var rows = ReadRows(connection, query);
        rows.Select(static row => row[1].AsInteger()).Should().Equal(30, 30, 60);
        AssertMatchesSqlite(rows, setup, query);
    }

    [Test]
    public void ExplicitRangeAndGroupsRunningFramesRouteThroughThePeerProgram()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (1, 20), (2, 5);",
        ];
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        foreach (var frame in new[]
                 {
                     "RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW",
                     "GROUPS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW",
                 })
        {
            var query = $"SELECT id, sum(v) OVER (ORDER BY id {frame}) FROM t ORDER BY id;";
            Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
                .Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
            AssertMatchesSqlite(ReadRows(connection, query), setup, query);
        }
    }

    [Test]
    public void RangeCurrentRowFrameRoutesThroughThePeerProgramAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (1, 20), (2, 5);",
        ];
        const string query =
            "SELECT id, sum(v) OVER (ORDER BY id RANGE BETWEEN CURRENT ROW AND CURRENT ROW) FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.NotContain("AggInverse").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void GroupsPrecedingFrameRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (1, 20), (2, 30), (2, 40), (3, 50);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("AggInverse").And.Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void GroupsPrecedingGroupConcatKeepsBufferedFallback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'b');");

        var query =
            "SELECT k, group_concat(v) OVER (ORDER BY k GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY k;";
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenWindowBuffer").And.Contain("WindowBufferCompute");
    }

    [Test]
    public void GroupsPrecedingMinRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 5);",
        ];
        const string query =
            "SELECT k, min(v) OVER (ORDER BY k GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void GroupsFollowingFrameRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k GROUPS BETWEEN CURRENT ROW AND 1 FOLLOWING) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("AggInverse").And.Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void GroupsTwoFollowingFrameMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k GROUPS BETWEEN CURRENT ROW AND 2 FOLLOWING) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangeExcludeGroupRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (1, 20), (2, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE GROUP) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangeExcludeTiesRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE TIES) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RowsExcludeGroupRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (1, 20), (2, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE GROUP) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RowsExcludeCurrentRowRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(v INTEGER);",
            "INSERT INTO t VALUES (10), (20), (30);",
        ];
        const string query =
            "SELECT v, sum(v) OVER (ORDER BY v ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE CURRENT ROW) " +
            "FROM t ORDER BY v;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RowsUnboundedFollowingFrameRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(v INTEGER);",
            "INSERT INTO t VALUES (10), (20), (30);",
        ];
        const string query =
            "SELECT v, sum(v) OVER (ORDER BY v ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) " +
            "FROM t ORDER BY v;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.Contain("AggInverse").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangeUnboundedFollowingFrameRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k RANGE BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.Contain("AggInverse").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void GroupsFullPartitionFrameRoutesAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k GROUPS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangeFollowingFrameRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (10, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k RANGE BETWEEN CURRENT ROW AND 1 FOLLOWING) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("AggInverse").And.Contain("OpenEphemeral").And.Contain("Compare")
            .And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangeFollowingDescendingFrameMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (10, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k DESC RANGE BETWEEN CURRENT ROW AND 1 FOLLOWING) " +
            "FROM t ORDER BY k DESC;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangePrecedingFrameRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (10, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) " +
            "FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Should().Contain("AggInverse").And.Contain("OpenEphemeral").And.Contain("Compare")
            .And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangePrecedingDescendingFrameMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (10, 30);",
        ];
        const string query =
            "SELECT k, sum(v) OVER (ORDER BY k DESC RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) " +
            "FROM t ORDER BY k DESC;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.Contain("OpenEphemeral").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RangePrecedingMultiKeyOrderByIsRejected()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 1, 10), (1, 2, 20);");

        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            connection,
            "SELECT a, sum(v) OVER (ORDER BY a, b RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t;"));
        error!.Message.Should().Contain("RANGE with offset");
    }

    [Test]
    public void RangePrecedingMinRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (10, 5);",
        ];
        const string query =
            "SELECT k, min(v) OVER (ORDER BY k RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY k;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void OnePrecedingRowsFrameRoutesThroughTheStreamingWindowProgram()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
        ];
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var query = $"SELECT id, sum(v) OVER (ORDER BY id {OnePrecedingFrame}) AS w FROM t ORDER BY id;";
        ReadRows(connection, query).Select(row => row[1]).Should().Equal(
            SqlValue.Integer(10), SqlValue.Integer(30), SqlValue.Integer(50), SqlValue.Integer(70));

        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.NotContain("WindowBufferCompute");
    }

    [Test]
    public void TwoPrecedingRowsFrameRoutesThroughInverseAndMatchesSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
        ];
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        const string query =
            "SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS w " +
            "FROM t ORDER BY id;";

        ReadRows(connection, query).Select(static row => row[1]).Should().Equal(
            SqlValue.Integer(10), SqlValue.Integer(30), SqlValue.Integer(60), SqlValue.Integer(90));
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.Contain("JumpIf").And.NotContain("WindowBufferCompute");
    }

    [Test]
    public void ThreePrecedingCountSumAndAvgRouteThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(grp TEXT, id INTEGER, v);",
            "INSERT INTO t VALUES ('a', 1, NULL), ('a', 2, 5), ('a', 3, 2.5), " +
            "('a', 4, '7'), ('a', 5, 1), ('b', 1, 10), ('b', 2, 20);",
        ];
        var query =
            "SELECT grp, id, count(*) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 3 PRECEDING AND CURRENT ROW), " +
            "sum(v) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 3 PRECEDING AND CURRENT ROW), " +
            "avg(v) OVER (PARTITION BY grp ORDER BY id ROWS BETWEEN 3 PRECEDING AND CURRENT ROW) " +
            "FROM t ORDER BY grp, id;";
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query)).ToList();
        opcodes.Count(static opcode => opcode == "AggInverse").Should().Be(3);
        opcodes.Should().Contain("JumpIf").And.NotContain("OpenWindowBuffer");

        var rows = ReadRows(connection, query);
        AssertMatchesSqlite(rows, setup, query);
    }

    [Test]
    public void OversizedPrecedingOffsetKeepsBufferedFallback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        var query =
            $"SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN {WindowFrameSpec.MaxStreamingPreceding + 1} PRECEDING AND CURRENT ROW) AS w " +
            "FROM t ORDER BY id;";

        ReadRows(connection, query).Select(static row => row[1]).Should().Equal(
            SqlValue.Integer(10), SqlValue.Integer(30));
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("WindowBufferCompute").And.NotContain("AggInverse");
    }

    [Test]
    public void OneFollowingSumRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
        ];
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        const string query =
            "SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING) AS w " +
            "FROM t ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.NotContain("WindowBufferCompute");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void PrecedingAndFollowingSumRoutesThroughInverseAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);",
        ];
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        const string query =
            "SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS w " +
            "FROM t ORDER BY id;";

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggInverse").And.NotContain("WindowBufferCompute");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RowsFullPartitionFrameRoutesThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        const string query =
            "SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS total " +
            "FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenEphemeral").And.NotContain("WindowBufferCompute");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void FilterClauseOnRunningFrameRoutesThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        var query =
            $"SELECT id, sum(v) FILTER (WHERE v > 15) OVER (ORDER BY id {RunningFrame}) AS running FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("FilterRegisters").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void FilterClauseOnMovingFrameKeepsBufferedFallback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        var query =
            "SELECT id, sum(v) FILTER (WHERE v > 15) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY id;";
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("OpenWindowBuffer").And.Contain("WindowBufferCompute");
    }

    [Test]
    public void GroupConcatWithLiteralSeparatorRoutesThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, label TEXT);",
            "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c');",
        ];
        var query = $"SELECT id, group_concat(label, '|') OVER (ORDER BY id {RunningFrame}) AS acc FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggStep").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RankingFunctionWindowRoutesThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (2, 20);",
        ];
        var query = "SELECT id, row_number() OVER (ORDER BY id) FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggStep").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void RankFunctionWindowRoutesThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (2, 20), (3, 30);",
        ];
        var query = "SELECT id, rank() OVER (ORDER BY id) FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggStep").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void DenseRankFunctionWindowRoutesThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (2, 20), (3, 30);",
        ];
        var query = "SELECT id, dense_rank() OVER (ORDER BY id) FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggStep").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void FirstAndLastValueOnRunningFrameRouteThroughStreamingAndMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(id INTEGER, v INTEGER);",
            "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);",
        ];
        var query =
            $"SELECT id, first_value(v) OVER (ORDER BY id {RunningFrame}), last_value(v) OVER (ORDER BY id {RunningFrame}) FROM t ORDER BY id;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("AggStep").And.NotContain("OpenWindowBuffer");
        AssertMatchesSqlite(ReadRows(connection, query), setup, query);
    }

    [Test]
    public void MixedRankingAndAggregateKeepsBufferedFallback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        var query =
            $"SELECT row_number() OVER (ORDER BY id {RunningFrame}), sum(v) OVER (ORDER BY id {RunningFrame}) FROM t ORDER BY id;";
        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("WindowBufferCompute");
    }

    [Test]
    public void LimitedRunningWindowRoutesWithGatedResultRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30), (4, 40);");

        var query = $"SELECT id, sum(v) OVER (ORDER BY id {RunningFrame}) AS running FROM t ORDER BY id LIMIT 2;";
        ReadRows(connection, query).Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(10)),
            (SqlValue.Integer(2), SqlValue.Integer(30)));

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("LimitGate");
    }

    [Test]
    public void OrderByMissingPartitionPrefixRoutesThroughTheBufferedWindowProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE sales(region TEXT, amount INTEGER);");
        Execute(connection, "INSERT INTO sales VALUES ('a', 10), ('b', 5), ('a', 30);");

        // The running-frame lowering needs the top ORDER BY to make partitions contiguous. A bare
        // "ORDER BY amount" is not partition-contiguous, so the buffered lowering owns it and sorts
        // the projected records after the window pass instead.
        var query =
            $"SELECT region, amount, sum(amount) OVER (PARTITION BY region ORDER BY amount {RunningFrame}) AS running " +
            "FROM sales ORDER BY amount;";
        ReadRows(connection, query).Select(row => (row[0], row[1], row[2])).Should().Equal(
            (SqlValue.Text("b"), SqlValue.Integer(5), SqlValue.Integer(5)),
            (SqlValue.Text("a"), SqlValue.Integer(10), SqlValue.Integer(10)),
            (SqlValue.Text("a"), SqlValue.Integer(30), SqlValue.Integer(40)));

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should().Contain("WindowBufferCompute");
    }

    [Test]
    public void PartitionedWindowWithoutTopOrderEmitsInPartitionOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(grp INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (1, 30);");

        // With no top-level ORDER BY SQLite emits a windowed SELECT in the first window's sort order —
        // its PARTITION BY keys ascending — so the buffered lowering sorts the projected records by the
        // partition key (stable, preserving scan order within each partition) rather than emitting raw
        // scan order.
        var query = $"SELECT grp, sum(v) OVER (PARTITION BY grp {RunningFrame}) AS running FROM t;";
        ReadRows(connection, query).Select(row => (row[0], row[1])).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(10)),
            (SqlValue.Integer(1), SqlValue.Integer(40)),
            (SqlValue.Integer(2), SqlValue.Integer(20)));

        Opcodes(ReadRows(connection, "EXPLAIN " + query)).Should()
            .Contain("WindowBufferCompute").And.Contain("OpenSorter");
    }

    // ---- Fallback boundaries (evaluator keeps ownership; EXPLAIN cannot describe them) ------

    [Test]
    public void WindowOverAJoinFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE l(id INTEGER, v INTEGER);");
        Execute(connection, "CREATE TABLE r(id INTEGER, w INTEGER);");
        Execute(connection, "INSERT INTO l VALUES (1, 10), (2, 20);");
        Execute(connection, "INSERT INTO r VALUES (1, 100), (2, 200);");

        // The window route claims exactly one base table; a join source keeps the evaluator.
        var query =
            "SELECT l.id, sum(r.w) OVER (ORDER BY l.id) FROM l JOIN r ON r.id = l.id ORDER BY l.id;";
        ReadRows(connection, query).Select(row => row[1]).Should().Equal(
            SqlValue.Integer(100), SqlValue.Integer(300));

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void DistinctWindowSelectFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(grp TEXT, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES ('a', 1), ('a', 1), ('b', 2);");

        // DISTINCT de-duplicates the projected rows after windowing; the route owns only the
        // window pipeline, so the evaluator keeps it.
        var query = "SELECT DISTINCT count(*) OVER (PARTITION BY grp) FROM t;";
        ReadRows(connection, query).Should().HaveCount(2);

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }


    [Test]
    public void DistinctWindowArgumentFallsBackAndEvaluatorRejects()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 10), (3, 20);");

        var query = $"SELECT id, sum(DISTINCT v) OVER (ORDER BY id {RunningFrame}) FROM t ORDER BY id;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void PercentileWindowFallsBackAndEvaluatorRejects()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);");

        var query = $"SELECT percentile(v, 50) OVER (ORDER BY id {RunningFrame}) FROM t;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowCombinedWithGroupByFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        // GROUP BY runs first and the window pass then runs over the grouped rows, which this
        // route cannot model, so the evaluator keeps ownership and produces SQLite's answer.
        var query = $"SELECT sum(v) OVER (ORDER BY id {RunningFrame}), count(*) FROM t GROUP BY id;";
        var rows = ReadRows(connection, query);
        rows.Should().HaveCount(2);
        rows[0][0].AsInteger().Should().Be(10);
        rows[0][1].AsInteger().Should().Be(1);
        rows[1][0].AsInteger().Should().Be(30);
        rows[1][1].AsInteger().Should().Be(1);

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void WindowInWhereClauseFallsBackAndEvaluatorRejects()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        var query = $"SELECT id FROM t WHERE sum(v) OVER (ORDER BY id {RunningFrame}) > 10;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void CompoundWindowTermFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        // A compound term that opens a window buffer is not a conservative term, so the whole
        // compound stays on the evaluator.
        var query =
            $"SELECT sum(v) OVER (ORDER BY id {RunningFrame}) FROM t UNION ALL SELECT v FROM t;";
        ReadRows(connection, query).Should().HaveCount(4);

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
    }

    [Test]
    public void PartitionCollationMatchesSqliteAndMissingCollationIsRejected()
    {
        string[] setup =
        [
            "CREATE TABLE t(value TEXT);",
            "INSERT INTO t VALUES ('A'), ('a'), ('B');",
        ];
        const string query =
            "SELECT value, count(*) OVER (PARTITION BY value COLLATE NOCASE) " +
            "FROM t ORDER BY value COLLATE NOCASE, value;";

        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var rows = ReadRows(connection, query);
        AssertMatchesSqlite(rows, setup, query);
        rows.Select(row => row[1].AsInteger()).Should().Equal(2, 2, 1);

        string[] nonContiguousSetup =
        [
            "CREATE TABLE t(value TEXT);",
            "INSERT INTO t VALUES ('A'), ('B'), ('a');",
        ];
        var nonContiguous =
            $"SELECT value, count(*) OVER (PARTITION BY value COLLATE NOCASE {RunningFrame}) " +
            "FROM t ORDER BY value;";
        using var nonContiguousConnection = new EmbeddedDatabase().Connect();
        foreach (var statement in nonContiguousSetup)
            Execute(nonContiguousConnection, statement);
        var nonContiguousRows = ReadRows(nonContiguousConnection, nonContiguous);
        AssertMatchesSqlite(nonContiguousRows, nonContiguousSetup, nonContiguous);
        nonContiguousRows[^1][1].Should().Be(SqlValue.Integer(2));
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(nonContiguousConnection, "EXPLAIN " + nonContiguous));

        var contiguous =
            $"SELECT value, count(*) OVER (PARTITION BY value COLLATE NOCASE {RunningFrame}) " +
            "FROM t ORDER BY value COLLATE NOCASE;";
        var contiguousRows = ReadRows(nonContiguousConnection, contiguous);
        AssertMatchesSqlite(contiguousRows, nonContiguousSetup, contiguous);
        Opcodes(ReadRows(nonContiguousConnection, "EXPLAIN " + contiguous))
            .Should().Contain("SameGroup");

        const string missing =
            "SELECT count(*) OVER (PARTITION BY value COLLATE missing) FROM t;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, missing))!
            .Message.Should().Be("no such collation sequence: missing");
    }

    [Test]
    public void DeclaredPartitionCollationRoutesWhileCustomCallbacksStayOnEvaluator()
    {
        string[] setup =
        [
            "CREATE TABLE t(value TEXT COLLATE NOCASE);",
            "INSERT INTO t VALUES ('A'), ('B'), ('a');",
        ];
        var declared =
            $"SELECT value, count(*) OVER (PARTITION BY value {RunningFrame}) " +
            "FROM t ORDER BY value;";
        using var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var declaredRows = ReadRows(connection, declared);
        AssertMatchesSqlite(declaredRows, setup, declared);
        declaredRows.Select(row => row[1].AsInteger()).Should().Equal(1, 2, 1);
        Opcodes(ReadRows(connection, "EXPLAIN " + declared))
            .Should().Contain("SameGroup");

        var database = new EmbeddedDatabase();
        database.RegisterCollation(
            "throwing",
            (_, _) => throw new InvalidOperationException("partition collation failed"));
        using var custom = database.Connect();
        Execute(custom, "CREATE TABLE t(value TEXT);");
        Execute(custom, "INSERT INTO t VALUES ('A'), ('a');");
        var customQuery =
            $"SELECT value, count(*) OVER (PARTITION BY value COLLATE throwing {RunningFrame}) " +
            "FROM t ORDER BY value COLLATE throwing;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(custom, "EXPLAIN " + customQuery));
        Assert.Throws<InvalidOperationException>(() => ReadRows(custom, customQuery))!
            .Message.Should().Be("partition collation failed");
    }

    [Test]
    public void DeclaredCustomWindowOrderAndDistinctStarPreserveEvaluatorSemantics()
    {
        var callbacks = 0;
        var database = new EmbeddedDatabase();
        database.RegisterCollation("observed", (left, right) =>
        {
            callbacks++;
            return string.CompareOrdinal(left, right);
        });
        using var custom = database.Connect();
        Execute(custom, "CREATE TABLE t(value TEXT COLLATE observed);");
        Execute(custom, "INSERT INTO t VALUES ('b'), ('a'), ('c');");
        var ordered =
            $"SELECT value, count(*) OVER (ORDER BY value {RunningFrame}) " +
            "FROM t ORDER BY value;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(custom, "EXPLAIN " + ordered));
        ReadRows(custom, ordered).Should().HaveCount(3);
        callbacks.Should().BeGreaterThan(0);

        string[] distinctSetup =
        [
            "CREATE TABLE t(value TEXT COLLATE NOCASE);",
            "INSERT INTO t VALUES ('x'), ('X');",
        ];
        const string distinct =
            "SELECT DISTINCT *, count(*) OVER () FROM t;";
        using var declared = new EmbeddedDatabase().Connect();
        foreach (var statement in distinctSetup)
            Execute(declared, statement);
        var rows = ReadRows(declared, distinct);
        AssertMatchesSqlite(rows, distinctSetup, distinct);
        rows.Should().ContainSingle();
    }

    [Test]
    public void LimitZeroStillValidatesDistinctWindowCalls()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Assert.Throws<EmbeddedSqlException>(
                () => ReadRows(
                    connection,
                    "SELECT count(DISTINCT value) OVER () FROM t LIMIT 0;"))!
            .Message.Should().Contain("DISTINCT is not supported for window functions");
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void AssertMatchesSqlite(IReadOnlyList<SqlValue[]> managed, IReadOnlyList<string> setup, string query)
    {
        var reference = RunSqlite(setup, query);
        managed.Should().HaveCount(reference.Count);
        for (var row = 0; row < reference.Count; row++)
        {
            managed[row].Should().HaveCount(reference[row].Length);
            for (var column = 0; column < reference[row].Length; column++)
                CellsShouldMatch(managed[row][column], reference[row][column]);
        }
    }

    private static List<object?[]> RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = query;
        using var reader = queryCommand.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(values);
        }

        return rows;
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference)
    {
        if (reference is null)
        {
            managed.Kind.Should().Be(SqlValueKind.Null);
            return;
        }

        switch (reference)
        {
            case long integer:
                ToDouble(managed).Should().Be(integer);
                break;
            case double real:
                ToDouble(managed).Should().BeApproximately(real, 1e-9);
                break;
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text);
                managed.AsText().Should().Be(text);
                break;
            default:
                managed.ToString().Should().Be(reference.ToString());
                break;
        }
    }

    private static double ToDouble(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => double.Parse(value.AsText(), CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Value {value.Kind} is not numeric."),
        };

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = statement.GetValue(ordinal);

            rows.Add(values);
        }

        return rows;
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var names = new string[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
            names[ordinal] = statement.GetColumnName(ordinal);

        return names;
    }
}
