using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes the supported aggregate SQL subset through the real
// AggReset/AggStep/AggFinalize opcode family (plus Goto/SameGroup for GROUP BY) and that the
// routed results stay byte-identical to the tree-walking evaluator and SQLite. EXPLAIN is used
// as the ground truth for "was this lowered to bytecode?": a routed statement dumps the real
// row collector, group-key, sorter, finalizer, and distinct opcodes, while deliberate callback
// or error-order fallbacks remain evaluator-owned.
public class AggregateSqlRoutingTests
{
    [Test]
    public void ScalarNumericAggregatesMatchEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (NULL), (30);");

        var rows = ReadRows(
            connection,
            "SELECT count(*), count(value), sum(value), avg(value), min(value), max(value), total(value) FROM t;");

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(4));
        rows[0][1].Should().Be(SqlValue.Integer(3));
        rows[0][2].Should().Be(SqlValue.Integer(60));
        rows[0][3].Should().Be(SqlValue.Real(20));
        rows[0][4].Should().Be(SqlValue.Integer(10));
        rows[0][5].Should().Be(SqlValue.Integer(30));
        rows[0][6].Should().Be(SqlValue.Real(60));
    }

    [Test]
    public void ScalarAggregatesOverEmptyTableYieldEvaluatorIdentities()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(
            connection,
            "SELECT count(*), count(value), sum(value), avg(value), min(value), max(value), total(value), group_concat(value) FROM t;");

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(0));
        rows[0][1].Should().Be(SqlValue.Integer(0));
        rows[0][2].Should().Be(SqlValue.Null);
        rows[0][3].Should().Be(SqlValue.Null);
        rows[0][4].Should().Be(SqlValue.Null);
        rows[0][5].Should().Be(SqlValue.Null);
        rows[0][6].Should().Be(SqlValue.Real(0));
        rows[0][7].Should().Be(SqlValue.Null);
    }

    [Test]
    public void GroupConcatConcatenatesInScanOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(name TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('b'), (NULL), ('c');");

        ReadRows(connection, "SELECT group_concat(name) FROM t;")[0][0]
            .Should().Be(SqlValue.Text("a,b,c"));
        ReadRows(connection, "SELECT group_concat(name, '-') FROM t;")[0][0]
            .Should().Be(SqlValue.Text("a-b-c"));
    }

    [Test]
    public void ScalarAggregateColumnLabelsUseAliasOrExpressionText()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // SQLite labels an unaliased aggregate with the verbatim expression text.
        ColumnNames(connection, "SELECT count(*) AS n, sum(value) FROM t;")
            .Should().Equal("n", "sum(value)");
    }

    [Test]
    public void GroupByEmitsGroupsInAscendingKeyOrderWithMultipleAggregates()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2, 20), (1, 10), (2, 5), (1, 7);");

        var rows = ReadRows(connection, "SELECT k, count(*), sum(v) FROM t GROUP BY k;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(17));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(25));
    }

    [Test]
    public void GroupByWithoutOrderByEmitsGroupsInSortedKeyOrder()
    {
        // SQLite aggregates through a sorter, so grouped queries without ORDER BY observe
        // groups in ascending key order (NULLs first, per-key collation). Queries relying on
        // this de-facto order (e.g. LIMIT over a grouped subquery) must match natively.
        string[] setup =
        [
            "CREATE TABLE t(k TEXT COLLATE NOCASE, v INTEGER);",
            "INSERT INTO t VALUES ('banana', 1), ('Apple', 2), (NULL, 3), ('cherry', 4), ('apple', 5), ('banana', 6);",
        ];

        AssertMatchesSqlite(setup, "SELECT k, count(*), sum(v) FROM t GROUP BY k;");
        AssertMatchesSqlite(setup, "SELECT k, count(*) FROM t GROUP BY k HAVING count(*) > 1;");
        AssertMatchesSqlite(setup, "SELECT k FROM t GROUP BY k LIMIT 2;");
        AssertMatchesSqlite(
            setup,
            "SELECT (SELECT max(v) FROM t i WHERE i.k IS o.k) FROM t o GROUP BY o.k LIMIT 1;");

        string[] multiKeySetup =
        [
            "CREATE TABLE m(a INTEGER, b TEXT, v REAL);",
            "INSERT INTO m VALUES (2, 'x', 1.5), (1, 'y', 2.5), (2, 'w', 3.5), (1, 'x', 4.5), (2, 'x', 5.5);",
        ];
        AssertMatchesSqlite(multiKeySetup, "SELECT a, b, count(*), sum(v) FROM m GROUP BY a, b;");
        AssertMatchesSqlite(multiKeySetup, "SELECT a, b FROM m GROUP BY a, b LIMIT 3;");
    }

    [Test]
    public void GroupByMultipleKeysGroupsOnTheKeyTuple()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b TEXT, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x', 10), (1, 'y', 20), (1, 'x', 5);");

        var rows = ReadRows(connection, "SELECT a, b, sum(v) FROM t GROUP BY a, b;");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("x"), SqlValue.Integer(15));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Text("y"), SqlValue.Integer(20));
    }

    [Test]
    public void GroupByNullKeysGroupTogether()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (NULL), (1), (NULL), (2);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k;");

        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Null, SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        rows[2].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void GroupByTreatsEqualNumericKeysAsOneGroup()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k);");
        Execute(connection, "INSERT INTO t VALUES (1), (1.0), (1);");

        var rows = ReadRows(connection, "SELECT count(*) FROM t GROUP BY k;");

        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(3));
    }

    [Test]
    public void WhereFiltersRowsBeforeAggregation()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (1, 1), (2, 30), (2, 2);");

        ReadRows(connection, "SELECT count(*), sum(v) FROM t WHERE v > 5;")[0]
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(40));

        var grouped = ReadRows(connection, "SELECT k, count(*) FROM t WHERE v > 5 GROUP BY k;");
        grouped.Should().HaveCount(2);
        grouped[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        grouped[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
    }

    [Test]
    public void ConstantProjectionRoutesAlongsideAggregate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        var rows = ReadRows(connection, "SELECT count(*), 42 FROM t;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(42));

        // Proves the whole statement (including the folded constant) went through the accumulator.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*), 42 FROM t;"))
            .Should().Contain("AggReset").And.Contain("AggStep").And.Contain("AggFinalize");
    }

    [Test]
    public void ScalarAggregateExplainEmitsTheAccumulatorProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN SELECT sum(value) FROM t;");

        Opcodes(rows).Should().Equal(
            "OpenReadCursor",
            "AggReset",
            "Rewind",
            "Column",
            "AggStep",
            "Next",
            "CloseCursor",
            "AggFinalize",
            "ResultRow",
            "Halt");

        Comments(rows).Should().Contain("reset accumulator 0")
            .And.Contain("accumulator 0=rows step r[0]")
            .And.Contain("r[1]=sum finalize accumulator 0");
    }

    [Test]
    public void NullaryCountExplainDescribesTheRowCountShortcut()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN SELECT count(*) FROM t;");

        // The bare `SELECT count(*) FROM t` shortcut emits a minimal O(1) program: open the
        // cursor, read its row count, emit one result row, halt. No scan loop, no accumulator.
        Opcodes(rows).Should().Equal(
            "OpenReadCursor",
            "RowCount",
            "ResultRow",
            "Halt");

        Comments(rows).Should().Contain("r[0]=c0.rowcount");
    }

    [Test]
    public void SelectCountStarReturnsTheRowCountWithoutScanning()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // Empty table → 0.
        ReadRows(connection, "SELECT count(*) FROM t;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));

        // Non-empty table → 3.
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");
        ReadRows(connection, "SELECT count(*) FROM t;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3));

        // Differential: matches Microsoft.Data.Sqlite for empty and non-empty tables.
        AssertMatchesSqlite(["CREATE TABLE t(value INTEGER);"], "SELECT count(*) FROM t;");
        AssertMatchesSqlite(
            ["CREATE TABLE t(value INTEGER);", "INSERT INTO t VALUES (1), (2), (3);"],
            "SELECT count(*) FROM t;");
    }

    [Test]
    public void CountStarWithWherePredicateStaysOnTheAccumulatorPath()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        // A WHERE predicate changes the counted rows, so the shortcut must not fire — the
        // statement must keep scanning and accumulating.
        var rows = ReadRows(connection, "EXPLAIN SELECT count(*) FROM t WHERE value > 1;");
        Opcodes(rows).Should().NotContain("RowCount");
        Opcodes(rows).Should().Contain("AggReset").And.Contain("AggStep").And.Contain("AggFinalize");

        // And the value is the filtered count, not the full row count.
        ReadRows(connection, "SELECT count(*) FROM t WHERE value > 1;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));
    }

    [Test]
    public void GroupedAggregateExplainEmitsGotoAndSameGroup()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT k, sum(v) FROM t GROUP BY k;")).ToList();

        opcodes.Should().Contain("AggReset")
            .And.Contain("AggStep")
            .And.Contain("AggFinalize")
            .And.Contain("Goto")
            .And.Contain("SameGroup");
    }

    [Test]
    public void WhereFilteredScalarAggregateExplainEmitsFilterAndAccumulator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*) FROM t WHERE value > 1;")).ToList();

        opcodes.Should().Contain("Filter")
            .And.Contain("AggReset")
            .And.Contain("AggStep")
            .And.Contain("AggFinalize");
    }

    [Test]
    public void GroupedAggregateResetReplayReflectsAppendedRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20), (1, 30);");

        using var statement = connection.Prepare("SELECT k, count(*), sum(v) FROM t GROUP BY k;");

        DrainGrouped(statement).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(40)),
            (SqlValue.Integer(2), SqlValue.Integer(1), SqlValue.Integer(20)));

        Execute(connection, "INSERT INTO t VALUES (2, 5), (3, 7);");

        statement.Reset();
        DrainGrouped(statement).Should().Equal(
            (SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(40)),
            (SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(25)),
            (SqlValue.Integer(3), SqlValue.Integer(1), SqlValue.Integer(7)));
    }

    [Test]
    public void GroupedAggregateHavingWithBoundedWindowRoutesThroughTheVdbe()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2, 20), (1, 10), (2, 5), (1, 7), (3, 9);");

        const string sql =
            "SELECT k AS group_key, count(*) AS n, sum(v) AS total FROM t " +
            "GROUP BY k HAVING count(*) >= ?1 LIMIT ?2 OFFSET ?3;";
        using var statement = connection.Prepare(sql);
        statement.Bind(1, SqlValue.Integer(2));
        statement.Bind(2, SqlValue.Integer(2));
        statement.Bind(3, SqlValue.Integer(1));

        statement.GetColumnName(0).Should().Be("group_key");
        statement.GetColumnName(1).Should().Be("n");
        statement.GetColumnName(2).Should().Be("total");
        var initialRows = DrainRows(statement);
        initialRows.Should().ContainSingle();
        initialRows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(25));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(3));
        statement.Bind(3, SqlValue.Integer(0));
        var reboundRows = DrainRows(statement);
        reboundRows.Should().HaveCount(3);
        reboundRows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(17));
        reboundRows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2), SqlValue.Integer(25));
        reboundRows[2].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(1), SqlValue.Integer(9));

        using var initialExplain = connection.Prepare("EXPLAIN " + sql);
        initialExplain.Bind(1, SqlValue.Integer(2));
        initialExplain.Bind(2, SqlValue.Integer(2));
        initialExplain.Bind(3, SqlValue.Integer(1));
        Opcodes(DrainRows(initialExplain))
            .Should().Contain("AggFinalize").And.Contain("FilterRegisters")
            .And.Contain("OffsetGate").And.Contain("LimitGate");

        using var reboundExplain = connection.Prepare("EXPLAIN " + sql);
        reboundExplain.Bind(1, SqlValue.Integer(1));
        reboundExplain.Bind(2, SqlValue.Integer(3));
        reboundExplain.Bind(3, SqlValue.Integer(0));
        Opcodes(DrainRows(reboundExplain))
            .Should().Contain("AggFinalize").And.Contain("FilterRegisters")
            .And.Contain("LimitGate").And.NotContain("OffsetGate");
    }

    [Test]
    public void OverflowCapableGroupedHavingPreservesNullTruthOnFallback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, NULL), (1, NULL), (2, 4), (2, NULL);");

        var rows = ReadRows(connection, "SELECT k, sum(v) FROM t GROUP BY k HAVING sum(v) IS NULL;");

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Null);
        const string query = "SELECT k, sum(v) FROM t GROUP BY k HAVING sum(v) IS NULL;";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void AggregateOrderByRoutesThroughTheOutputSorter()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (2), (3), (3), (3);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k HAVING count(*) >= 1 ORDER BY count(*) DESC;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Integer(3), SqlValue.Integer(2), SqlValue.Integer(1));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN SELECT k, count(*) FROM t GROUP BY k HAVING count(*) >= 1 ORDER BY count(*) DESC;"))
            .Should().Contain("GroupKey").And.Contain("SorterSort").And.Contain("AggFinalize");
    }

    [Test]
    public void HavingWithNonBareAggregateArgumentFallsBackAfterLowerableProjection()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 1), (1, 2), (2, 1);");

        var rows = ReadRows(connection, "SELECT k, sum(v) FROM t GROUP BY k HAVING sum(v + 1) >= 5;");

        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(3));
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT k, sum(v) FROM t GROUP BY k HAVING sum(v + 1) >= 5;"));
    }

    [Test]
    public void RejectedHavingErrorLeavesTheTransactionAvailableForRollback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (2);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k HAVING count(*) > missing;"))!;
        error.Message.Should().Be("no such column: missing");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT k, count(*) FROM t GROUP BY k HAVING count(*) > missing;"));

        Execute(connection, "ROLLBACK;");
        ReadRows(connection, "SELECT count(*) FROM t;")[0][0].Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void DistinctAggregateRoutesThroughTheResultSet()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2), (3), (3);");

        ReadRows(connection, "SELECT count(DISTINCT v) FROM t;")[0][0].Should().Be(SqlValue.Integer(3));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT count(DISTINCT v) FROM t;"))
            .Should().Contain("AggStep").And.Contain("AggFinalize");
    }

    [Test]
    public void CompositeAggregateExpressionRoutesThroughAFinalizer()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20);");

        ReadRows(connection, "SELECT sum(v) + 1 FROM t;")[0][0].Should().Be(SqlValue.Integer(31));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT sum(v) + 1 FROM t;"))
            .Should().Contain("AggFinalize");
    }

    [Test]
    public void GroupKeyOnlyProjectionRoutesThroughTheGroupingProgram()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (1), (2), (1);");

        ReadRows(connection, "SELECT k FROM t GROUP BY k;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT k FROM t GROUP BY k;"))
            .Should().Contain("GroupKey").And.Contain("AggStep").And.Contain("AggFinalize");
    }

    [Test]
    public void ScalarAggregateBareColumnUsesFirstScannedRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(label TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES ('first', 10), ('second', 20);");

        ReadRows(connection, "SELECT label, count(*) FROM t;")[0]
            .Should().Equal(SqlValue.Text("first"), SqlValue.Integer(2));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT label, count(*) FROM t;"))
            .Should().Contain("AggFinalize");
    }

    [Test]
    public void MinAndMaxSelectTheirFirstExtremumRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(label TEXT, value INTEGER);");
        Execute(
            connection,
            "INSERT INTO t VALUES ('ten', 10), ('thirty-first', 30), ('thirty-second', 30), ('five', 5);");

        ReadRows(connection, "SELECT label, min(value) FROM t;")[0]
            .Should().Equal(SqlValue.Text("five"), SqlValue.Integer(5));
        ReadRows(connection, "SELECT label, max(value) FROM t;")[0]
            .Should().Equal(SqlValue.Text("thirty-first"), SqlValue.Integer(30));
    }

    [Test]
    public void LastExtremumAggregateControlsBareColumns()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(label TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES ('low', 1), ('high', 9);");

        ReadRows(connection, "SELECT label, min(value), max(value) FROM t;")[0]
            .Should().Equal(SqlValue.Text("high"), SqlValue.Integer(1), SqlValue.Integer(9));
        ReadRows(connection, "SELECT label, max(value), min(value) FROM t;")[0]
            .Should().Equal(SqlValue.Text("low"), SqlValue.Integer(9), SqlValue.Integer(1));
    }

    [Test]
    public void GroupedBareColumnUsesRepresentativeWithinEachGroup()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, label TEXT, value INTEGER);");
        Execute(
            connection,
            "INSERT INTO t VALUES (1, 'one-first', 10), (1, 'one-max', 30),"
                + " (2, 'two-max', 20), (2, 'two-second', 5);");

        var rows = ReadRows(connection, "SELECT k, label, max(value) FROM t GROUP BY k;");

        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("one-max"), SqlValue.Integer(30));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("two-max"), SqlValue.Integer(20));
        ReadRows(connection, "SELECT k, label, count(*) FROM t GROUP BY k;")
            .Select(row => row[1])
            .Should().Equal(SqlValue.Text("one-first"), SqlValue.Text("two-max"));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT k, label, max(value) FROM t GROUP BY k;"))
            .Should().Contain("GroupKey").And.Contain("AggFinalize");
    }

    [Test]
    public void EmptyAndAllNullExtremaMatchSqliteBareColumnRules()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(label TEXT, value INTEGER);");

        ReadRows(connection, "SELECT label, count(*) FROM t;")[0]
            .Should().Equal(SqlValue.Null, SqlValue.Integer(0));

        Execute(connection, "INSERT INTO t VALUES ('first', NULL), ('last', NULL);");
        ReadRows(connection, "SELECT label, max(value) FROM t;")[0]
            .Should().Equal(SqlValue.Text("last"), SqlValue.Null);
    }

    [Test]
    public void HiddenExtremumAggregatesAlsoSelectTheRepresentativeRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(label TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES ('low', 1), ('high', 9);");

        ReadRows(connection, "SELECT label, count(*) FROM t HAVING max(value) > 0;")[0]
            .Should().Equal(SqlValue.Text("high"), SqlValue.Integer(2));
        ReadRows(connection, "SELECT label, count(*) FROM t ORDER BY min(value);")[0]
            .Should().Equal(SqlValue.Text("low"), SqlValue.Integer(2));
        ReadRows(connection, "SELECT label, max(value) FILTER (WHERE value < 9) FROM t;")[0]
            .Should().Equal(SqlValue.Text("low"), SqlValue.Integer(1));
        ReadRows(
            connection,
            "SELECT label, count(*) FROM t HAVING min(value) > 0 ORDER BY max(value);")[0]
            .Should().Equal(SqlValue.Text("low"), SqlValue.Integer(2));
    }

    [Test]
    public void ComputedKeysArgumentsFiltersDistinctParametersAndCollationsMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(k TEXT, v INTEGER);",
            "INSERT INTO t VALUES ('Alpha', 1), ('alpha', 2), ('Beta', 2), ('beta', NULL);",
        ];
        const string query =
            "SELECT lower(k) COLLATE NOCASE AS group_key, " +
            "sum(v * ?1) FILTER (WHERE v > ?2) AS total, count(DISTINCT v % 2) AS parity_count " +
            "FROM t GROUP BY lower(k) COLLATE NOCASE " +
            "ORDER BY count(*) DESC, min(k COLLATE NOCASE);";

        AssertMatchesSqlite(setup, query, SqlValue.Integer(3), SqlValue.Integer(1));

        using var connection = OpenManaged(setup);
        var rows = ReadRows(
            connection,
            query,
            SqlValue.Integer(3),
            SqlValue.Integer(1));
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Text("alpha"),
            SqlValue.Integer(6),
            SqlValue.Integer(2));
        rows[1].Should().Equal(
            SqlValue.Text("beta"),
            SqlValue.Integer(6),
            SqlValue.Integer(1));

        Opcodes(ReadRows(
                connection,
                "EXPLAIN " + query,
                SqlValue.Integer(3),
                SqlValue.Integer(1)))
            .Should().Contain("GroupKey").And.Contain("AggStep")
            .And.Contain("AggFinalize").And.Contain("SorterSort");
    }

    [Test]
    public void GroupedResultDistinctRunsAfterAggregateOrdering()
    {
        string[] setup =
        [
            "CREATE TABLE t(k INTEGER);",
            "INSERT INTO t VALUES (1), (1), (2), (2), (3);",
        ];
        const string query =
            "SELECT DISTINCT count(*) AS n FROM t GROUP BY k ORDER BY n DESC;";

        AssertMatchesSqlite(setup, query);

        using var connection = OpenManaged(setup);
        ReadRows(connection, query)
            .Select(row => row[0])
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
        Opcodes(ReadRows(connection, "EXPLAIN " + query))
            .Should().Contain("DistinctGate").And.Contain("SorterSort");

        const string bounded =
            "SELECT DISTINCT count(*) AS n FROM t GROUP BY k ORDER BY n DESC LIMIT 1 OFFSET 1;";
        AssertMatchesSqlite(setup, bounded);
        ReadRows(connection, bounded)[0][0].Should().Be(SqlValue.Integer(1));
        Opcodes(ReadRows(connection, "EXPLAIN " + bounded))
            .Should().Contain("DistinctGate").And.Contain("OffsetGate").And.Contain("LimitGate");
    }

    [Test]
    public void TiedGroupedOrderingMatchesEvaluatorSortedKeyOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(
            connection,
            "INSERT INTO t VALUES " +
            string.Join(", ", Enumerable.Range(1, 20).Reverse().Select(value => $"({value})")) +
            ";");
        const string compiled =
            "SELECT k, count(*) FROM t GROUP BY k ORDER BY count(*);";
        const string fallback =
            "SELECT k, count(*) FROM (SELECT k FROM t) GROUP BY k ORDER BY count(*);";

        var compiledRows = ReadRows(connection, compiled);
        var fallbackRows = ReadRows(connection, fallback);

        compiledRows.Should().HaveCount(20);
        for (var index = 0; index < compiledRows.Count; index++)
            compiledRows[index].Should().Equal(fallbackRows[index]);
        compiledRows.Select(row => row[0].AsInteger())
            .Should().Equal(Enumerable.Range(1, 20).Select(value => (long)value));
    }

    [Test]
    public void GroupedAggregateTermsRelocateInsideUnionAll()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE left_rows(k INTEGER);");
        Execute(connection, "CREATE TABLE right_rows(k INTEGER);");
        Execute(connection, "INSERT INTO left_rows VALUES (2), (1), (2);");
        Execute(connection, "INSERT INTO right_rows VALUES (1), (2), (1), (2);");
        const string query =
            "SELECT k, count(*) FROM left_rows GROUP BY k " +
            "UNION ALL SELECT k, count(*) FROM right_rows GROUP BY k;";

        var rows = ReadRows(connection, query);

        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));
        rows[2].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        rows[3].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));
        Opcodes(ReadRows(connection, "EXPLAIN " + query))
            .Count(opcode => opcode == "GroupKey").Should().Be(2);

        const string distinctQuery =
            "SELECT k, count(*) FROM left_rows GROUP BY k " +
            "UNION SELECT k, count(*) FROM right_rows GROUP BY k;";
        ReadRows(connection, distinctQuery).Should().HaveCount(3);
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN " + distinctQuery));
    }

    [Test]
    public void EmptyAndNullCollatedAggregatesMatchSqlite()
    {
        string[] emptySetup = ["CREATE TABLE t(k TEXT, v TEXT);"];
        const string scalar =
            "SELECT count(DISTINCT v COLLATE NOCASE), min(v COLLATE NOCASE), max(v COLLATE NOCASE) FROM t;";
        AssertMatchesSqlite(emptySetup, scalar);

        string[] groupedSetup =
        [
            "CREATE TABLE t(k TEXT, v TEXT);",
            "INSERT INTO t VALUES ('a', 'z'), ('A', 'Z'), (NULL, NULL), (NULL, 'x');",
        ];
        const string grouped =
            "SELECT k COLLATE NOCASE, count(*), min(v COLLATE NOCASE), max(v COLLATE NOCASE) " +
            "FROM t GROUP BY k COLLATE NOCASE ORDER BY k COLLATE NOCASE;";
        AssertMatchesSqlite(groupedSetup, grouped);

        using var connection = OpenManaged(groupedSetup);
        Opcodes(ReadRows(connection, "EXPLAIN " + grouped))
            .Should().Contain("GroupKey").And.Contain("AggFinalize");
    }

    [Test]
    public void IntegerOverflowMatchesEvaluatorAndSqliteWhileRemainingCompiled()
    {
        string[] setup =
        [
            "CREATE TABLE t(v INTEGER);",
            "INSERT INTO t VALUES (9223372036854775807), (1);",
        ];

        using var connection = OpenManaged(setup);
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "SELECT sum(v) FROM t;"))!
            .Message.Should().Contain("integer overflow");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT sum(v) FROM (SELECT v FROM t);"))!
            .Message.Should().Contain("integer overflow");
        Opcodes(ReadRows(connection, "EXPLAIN SELECT sum(v) FROM t;"))
            .Should().Contain("AggStep").And.Contain("AggFinalize");

        using var sqlite = OpenSqlite(setup);
        using var command = sqlite.CreateCommand();
        command.CommandText = "SELECT sum(v) FROM t;";
        Assert.Throws<MsData.SqliteException>(() => command.ExecuteScalar())!
            .Message.Should().Contain("integer overflow");
    }

    [Test]
    public void HiddenOverflowingOrderOnARejectedGroupRemainsEvaluatorOwned()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(
            connection,
            "INSERT INTO t VALUES (1, 1), (2, 9223372036854775807), (2, 1);");
        const string query =
            "SELECT k, count(*) FROM t GROUP BY k HAVING k = 1 ORDER BY sum(v);";

        ReadRows(connection, query).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void BoundedDistinctWithCustomCollationPreservesLateErrorsOnFallback()
    {
        var database = new EmbeddedDatabase();
        database.RegisterCollation("boomcmp", (left, right) =>
        {
            if (left == "boom" || right == "boom")
                throw new InvalidOperationException("collation failed");
            return string.CompareOrdinal(left, right);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a'), ('b'), ('boom');");
        const string query =
            "SELECT DISTINCT k COLLATE boomcmp FROM t GROUP BY k LIMIT 1;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        Assert.Throws<InvalidOperationException>(() => ReadRows(connection, query))!
            .Message.Should().Be("collation failed");
    }

    [Test]
    public void NestedCustomOrderCollationOnRejectedGroupsRemainsEvaluatorOwned()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterCollation(
            "boomcmp",
            (left, right) =>
            {
                calls++;
                return string.CompareOrdinal(left, right);
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'x'), (2, 'y');");
        const string query =
            "SELECT k, count(*) FROM t GROUP BY k HAVING k = 1 " +
            "ORDER BY (group_concat(v) COLLATE boomcmp) = 'x';";

        // SQLite evaluates the ORDER BY expression itself, so the COLLATE-qualified comparison
        // inside it invokes the registered collation exactly once even for a single group.
        ReadRows(connection, query).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        calls.Should().Be(1);

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void GroupedSumHavingDoesNotPreemptLaterProjectionErrors()
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "explode",
            1,
            _ => throw new InvalidOperationException("projection failed"));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(
            connection,
            "INSERT INTO t VALUES (1, 9223372036854775807), (1, 1), (2, 1);");
        const string query =
            "SELECT k, CASE WHEN k = 2 THEN explode(v) ELSE count(*) END " +
            "FROM t GROUP BY k HAVING sum(v) > 0;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        Assert.Throws<InvalidOperationException>(() => ReadRows(connection, query))!
            .Message.Should().Be("projection failed");
    }

    [Test]
    public void AggregateOrderValidationPrecedesLimitCallbacks()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe_limit", 0, _ =>
        {
            calls++;
            return SqlValue.Integer(1);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        const string query =
            "SELECT k, count(*) FROM t GROUP BY k " +
            "ORDER BY count(*) COLLATE missing LIMIT observe_limit();";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("no such collation sequence: missing");
        calls.Should().Be(0);

        const string windowQuery =
            "SELECT sum(k) OVER (ORDER BY k COLLATE missing) FROM t LIMIT observe_limit();";
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, windowQuery))!
            .Message.Should().Be("no such collation sequence: missing");
        calls.Should().Be(0);

        const string evaluatorFallback =
            "SELECT k, count(*) FROM t GROUP BY k HAVING sum(k) > 0 " +
            "ORDER BY count(*) LIMIT observe_limit();";
        ReadRows(connection, evaluatorFallback).Should().ContainSingle();
        calls.Should().Be(1);
    }

    [Test]
    public void InvalidAggregateLimitCallbackRunsOnlyOnce()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("invalid_limit", 0, _ =>
        {
            calls++;
            return SqlValue.Text("bad");
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        Assert.Throws<EmbeddedSqlException>(
                () => ReadRows(connection, "SELECT count(*) FROM t LIMIT invalid_limit();"))!
            .Message.Should().Be("datatype mismatch");
        calls.Should().Be(1);
    }

    [Test]
    public void ZeroAggregateLimitCallbackRunsOnceAndEmitsNoRows()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("changing_limit", 0, _ =>
        {
            calls++;
            return SqlValue.Integer(calls == 1 ? 0 : 1);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        ReadRows(connection, "SELECT count(*) FROM t LIMIT changing_limit();")
            .Should().BeEmpty();
        calls.Should().Be(1);
    }

    [Test]
    public void AggregateGroupKeyErrorsFollowTheWherePhaseOnFallback()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe_where", 1, arguments =>
        {
            calls++;
            return arguments[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        const string query =
            "SELECT count(*) FROM t WHERE observe_where(v) > 0 GROUP BY sum(v);";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, query))!
            .Message.Should().Be("no such function: SUM");
        calls.Should().Be(2);
    }

    [Test]
    public void NestedCastCollationsControlExtremaAndGrouping()
    {
        string[] extremaSetup =
        [
            "CREATE TABLE t(v TEXT);",
            "INSERT INTO t VALUES ('Z'), ('a');",
        ];
        const string extrema =
            "SELECT min(cast(v COLLATE NOCASE AS TEXT)), max(cast(v COLLATE NOCASE AS TEXT)) FROM t;";
        AssertMatchesSqlite(extremaSetup, extrema);

        string[] groupingSetup =
        [
            "CREATE TABLE t(v TEXT);",
            "INSERT INTO t VALUES ('A'), ('a'), ('B');",
        ];
        const string grouping =
            "SELECT cast(v COLLATE NOCASE AS TEXT), count(*) FROM t " +
            "GROUP BY cast(v COLLATE NOCASE AS TEXT);";
        AssertMatchesSqlite(groupingSetup, grouping);

        using var connection = OpenManaged(groupingSetup);
        ReadRows(connection, grouping).Should().HaveCount(2);
        Opcodes(ReadRows(connection, "EXPLAIN " + grouping))
            .Should().Contain("GroupKey");
    }

    [Test]
    public void DeclaredColumnCollationControlsAggregateSemantics()
    {
        string[] setup =
        [
            "CREATE TABLE t(v TEXT COLLATE NOCASE);",
            "INSERT INTO t VALUES ('A'), ('a'), ('B');",
        ];
        const string scalar =
            "SELECT count(DISTINCT v), min(v), max(v) FROM t;";
        const string grouped =
            "SELECT v, count(*) FROM t GROUP BY v ORDER BY v;";

        AssertMatchesSqlite(setup, scalar);
        AssertMatchesSqlite(setup, grouped);

        using var connection = OpenManaged(setup);
        var scalarRow = ReadRows(connection, scalar)[0];
        scalarRow.Should().Equal(
            SqlValue.Integer(2),
            SqlValue.Text("A"),
            SqlValue.Text("B"));
        ReadRows(connection, grouped).Should().HaveCount(2);
        Opcodes(ReadRows(connection, "EXPLAIN " + grouped))
            .Should().Contain("GroupKey");
    }

    [Test]
    public void ExplicitCollationPrecedenceAndCorrelatedDeclaredCollationMatchSqlite()
    {
        string[] precedenceSetup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE);",
            "INSERT INTO t VALUES ('A'), ('a');",
        ];
        const string precedence =
            "SELECT count(*) FROM t WHERE a = 'a' COLLATE BINARY;";
        const string nested =
            "SELECT (a COLLATE NOCASE) || '', count(*) FROM t " +
            "GROUP BY (a COLLATE NOCASE) || '';";
        AssertMatchesSqlite(precedenceSetup, precedence);
        AssertMatchesSqlite(precedenceSetup, nested);

        string[] correlatedSetup =
        [
            "CREATE TABLE outer_rows(a TEXT COLLATE NOCASE);",
            "CREATE TABLE inner_rows(x INTEGER);",
            "INSERT INTO outer_rows VALUES ('x');",
            "INSERT INTO inner_rows VALUES (1);",
        ];
        const string correlated =
            "SELECT (SELECT count(*) FROM inner_rows WHERE outer_rows.a = 'X') FROM outer_rows;";
        AssertMatchesSqlite(correlatedSetup, correlated);

        string[] compoundSetup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE);",
            "INSERT INTO t VALUES ('X');",
        ];
        const string compound = "SELECT 'x' UNION SELECT a FROM t;";
        using var compoundConnection = OpenManaged(compoundSetup);
        ReadRows(compoundConnection, compound).Should().ContainSingle();
        using var sqlite = OpenSqlite(compoundSetup);
        using var command = sqlite.CreateCommand();
        command.CommandText = compound;
        using var reader = command.ExecuteReader();
        var sqliteCount = 0;
        while (reader.Read())
            sqliteCount++;
        sqliteCount.Should().Be(1);
    }

    [Test]
    public void DeclaredCustomCollationInDeferredOrderStaysOnEvaluator()
    {
        var database = new EmbeddedDatabase();
        database.RegisterCollation(
            "throwing",
            (_, _) => throw new InvalidOperationException("declared collation failed"));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(g INTEGER, v TEXT COLLATE throwing);");
        Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'a'), (2, 'b');");
        const string query =
            "SELECT g, count(*) FROM t GROUP BY g HAVING g = 1 " +
            "ORDER BY count(DISTINCT v);";

        ReadRows(connection, query).Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
    }

    [Test]
    public void FilterCollationDoesNotLeakIntoAggregateResultDistinctness()
    {
        string[] setup =
        [
            "CREATE TABLE t(g INTEGER, v TEXT);",
            "INSERT INTO t VALUES (1, 'a'), (2, 'A');",
        ];
        const string query =
            "SELECT DISTINCT group_concat(v) " +
            "FILTER (WHERE 'x' COLLATE NOCASE = 'x') FROM t GROUP BY g;";

        AssertMatchesSqlite(setup, query);
        using var connection = OpenManaged(setup);
        ReadRows(connection, query).Should().HaveCount(2);
        Opcodes(ReadRows(connection, "EXPLAIN " + query))
            .Should().Contain("DistinctGate");
    }

    [Test]
    public void MixedCompoundAndCteShadowingPreserveLexicalCollations()
    {
        var callbacks = 0;
        var database = new EmbeddedDatabase();
        database.RegisterCollation("observed", (left, right) =>
        {
            callbacks++;
            return string.CompareOrdinal(left, right);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a TEXT COLLATE observed);");
        Execute(connection, "INSERT INTO t VALUES ('X');");
        const string compound =
            "SELECT 'x' UNION SELECT 'X' UNION ALL SELECT a FROM t;";

        ReadRows(connection, compound).Should().HaveCount(3);
        callbacks.Should().Be(0);

        string[] shadowSetup =
        [
            "CREATE TABLE t(a TEXT COLLATE BINARY);",
            "INSERT INTO t VALUES ('x');",
        ];
        const string shadowed =
            "SELECT (WITH t(a) AS (SELECT 'X' COLLATE NOCASE) " +
            "SELECT count(*) FROM t WHERE outer_t.a = 'X') FROM t AS outer_t;";
        AssertMatchesSqlite(shadowSetup, shadowed);
    }

    [Test]
    public void CollationsApplyToInBetweenCaseNullIfAndScalarExtrema()
    {
        const string comparisons =
            "SELECT ('A' COLLATE NOCASE) IN ('a'), " +
            "('A' COLLATE NOCASE) BETWEEN 'a' AND 'a', " +
            "CASE 'A' COLLATE NOCASE WHEN 'a' THEN 1 ELSE 0 END, " +
            "NULLIF('A', 'a' COLLATE NOCASE), " +
            "min('Z', 'a' COLLATE NOCASE), max('Z', 'a' COLLATE NOCASE);";
        AssertMatchesSqlite([], comparisons);

        string[] setup =
        [
            "CREATE TABLE t(v INTEGER);",
            "INSERT INTO t VALUES (1);",
        ];
        const string aggregate =
            "SELECT count(NULLIF('A', 'a' COLLATE NOCASE)) FROM t;";
        AssertMatchesSqlite(setup, aggregate);
    }

    [Test]
    public void ImplicitBinaryAndScalarExtremaUseLeftToRightCollationPrecedence()
    {
        string[] comparisonSetup =
        [
            "CREATE TABLE left_rows(a TEXT);",
            "CREATE TABLE right_rows(b TEXT COLLATE NOCASE);",
            "INSERT INTO left_rows VALUES ('x');",
            "INSERT INTO right_rows VALUES ('X');",
        ];
        const string comparison =
            "SELECT left_rows.a = right_rows.b, right_rows.b = left_rows.a " +
            "FROM left_rows, right_rows;";
        const string compound =
            "SELECT a FROM left_rows UNION SELECT b FROM right_rows;";
        AssertMatchesSqlite(comparisonSetup, comparison);
        using var comparisonConnection = OpenManaged(comparisonSetup);
        ReadRows(comparisonConnection, compound).Should().HaveCount(2);
        using var comparisonSqlite = OpenSqlite(comparisonSetup);
        using var compoundCommand = comparisonSqlite.CreateCommand();
        compoundCommand.CommandText = compound;
        using var compoundReader = compoundCommand.ExecuteReader();
        var compoundCount = 0;
        while (compoundReader.Read())
            compoundCount++;
        compoundCount.Should().Be(2);

        string[] extremaSetup =
        [
            "CREATE TABLE t(a TEXT);",
            "INSERT INTO t VALUES ('Z');",
        ];
        const string extrema =
            "SELECT min(a, 'a' COLLATE NOCASE), max(a, 'a' COLLATE NOCASE) FROM t;";
        AssertMatchesSqlite(extremaSetup, extrema);

        using var connection = OpenManaged(extremaSetup);
        ReadRows(connection, "SELECT min(a, 'a' COLLATE missing) FROM t;")[0][0]
            .Should().Be(SqlValue.Text("Z"));
    }

    [Test]
    public void NoCaseHashMatchesEmbeddedNullEquality()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value TEXT COLLATE NOCASE);");
        using (var insert = connection.Prepare("INSERT INTO t VALUES (?1), (?2);"))
        {
            insert.Bind(1, SqlValue.Text("A\0x"));
            insert.Bind(2, SqlValue.Text("a\0y"));
            insert.Step().Should().Be(StatementStepResult.Done);
        }

        var grouped = ReadRows(connection, "SELECT count(*) FROM t GROUP BY value;");
        grouped.Should().ContainSingle();
        grouped[0][0].Should().Be(SqlValue.Integer(2));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*) FROM t GROUP BY value;"))
            .Should().Contain("GroupKey");

        ReadRows(
                connection,
                "SELECT count(*) OVER (PARTITION BY value) FROM t ORDER BY value;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(2, 2);
    }

    [Test]
    public void ExplainDoesNotExecuteAggregateLimitCallbacks()
    {
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("tick", 0, _ =>
        {
            calls++;
            return SqlValue.Integer(1);
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");
        const string query = "SELECT count(*) FROM t LIMIT tick();";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + query)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        calls.Should().Be(0);
    }

    [Test]
    public void MixedCompoundCarriesCollationOnlyAcrossComparingOperators()
    {
        string[] setup =
        [
            "CREATE TABLE t(a TEXT COLLATE NOCASE);",
            "INSERT INTO t VALUES ('A');",
        ];
        const string noRetroactive =
            "SELECT 'x' UNION SELECT 'X' UNION ALL SELECT a FROM t;";
        const string carried =
            "SELECT 'a' UNION ALL SELECT 'a' COLLATE NOCASE UNION SELECT 'A';";

        using var connection = OpenManaged(setup);
        ReadRows(connection, noRetroactive).Should().HaveCount(3);
        ReadRows(connection, carried).Should().ContainSingle();
    }

    [Test]
    public void MissingGroupCollationIsValidatedAcrossCompilerAndEvaluatorRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('a');");
        const string compiled =
            "SELECT k, count(*) FROM t GROUP BY k COLLATE missing;";
        const string evaluator =
            "SELECT k, count(*) FROM (SELECT k FROM t) GROUP BY k COLLATE missing;";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, compiled))!
            .Message.Should().Be("no such collation sequence: missing");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, evaluator))!
            .Message.Should().Be("no such collation sequence: missing");
    }

    [Test]
    public void CallbackErrorsRouteOnlyWhenTheirPhaseOrderMatches()
    {
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "explode_scalar",
            1,
            _ => throw new InvalidOperationException("scalar aggregate argument failed"));
        database.RegisterAggregateFunction(
            "explode_aggregate",
            1,
            SqlValue.Integer(0),
            (_, _) => throw new InvalidOperationException("aggregate step failed"),
            value => value);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, 10), (2, 20);");

        Opcodes(ReadRows(connection, "EXPLAIN SELECT sum(explode_scalar(v)) FROM t;"))
            .Should().Contain("AggFinalize");
        Assert.Throws<InvalidOperationException>(
                () => ReadRows(connection, "SELECT sum(explode_scalar(v)) FROM t;"))!
            .Message.Should().Be("scalar aggregate argument failed");

        Opcodes(ReadRows(connection, "EXPLAIN SELECT explode_aggregate(v) FROM t;"))
            .Should().Contain("AggFinalize");
        Assert.Throws<InvalidOperationException>(
                () => ReadRows(connection, "SELECT explode_aggregate(v) FROM t;"))!
            .Message.Should().Be("aggregate step failed");

        const string unsafeHaving =
            "SELECT k, count(*) FROM t GROUP BY k HAVING explode_aggregate(v) > 0;";
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN " + unsafeHaving));
        ReadRows(connection, "EXPLAIN QUERY PLAN " + unsafeHaving)[0][3]
            .Should().Be(SqlValue.Text("MANAGED EVALUATOR FALLBACK"));
        Assert.Throws<InvalidOperationException>(() => ReadRows(connection, unsafeHaving))!
            .Message.Should().Be("aggregate step failed");
    }

    [Test]
    public void ObservableWhereAndGroupKeyPhasesRemainEvaluatorOwned()
    {
        var calls = new List<string>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe_where", 1, arguments =>
        {
            calls.Add($"where:{arguments[0].AsInteger()}");
            return arguments[0];
        });
        database.RegisterScalarFunction("observe_group", 1, arguments =>
        {
            calls.Add($"group:{arguments[0].AsInteger()}");
            return arguments[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");
        const string query =
            "SELECT observe_group(k), count(*) FROM t " +
            "WHERE observe_where(k) > 0 GROUP BY observe_group(k);";

        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN " + query));
        var rows = ReadRows(connection, query);

        rows.Should().HaveCount(2);
        calls.Should().Equal(
            "where:1",
            "where:2",
            "group:1",
            "group:2",
            "group:1",
            "group:2");
    }

    [Test]
    public void CancelledComputedGroupingCanResetAndReplay()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("cancel_group", 1, arguments =>
        {
            calls++;
            if (calls == 2)
                cancellation.Cancel();
            return arguments[0];
        });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2);");
        const string query =
            "SELECT cancel_group(k), count(*) FROM t GROUP BY cancel_group(k);";
        using var statement = connection.Prepare(query);

        Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));

        statement.Reset();
        var rows = DrainRows(statement);
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));

        using var plan = connection.Prepare("EXPLAIN QUERY PLAN " + query);
        using var cancelable = new CancellationTokenSource();
        plan.Step(cancelable.Token).Should().Be(StatementStepResult.Row);
        plan.GetValue(3).Should().Be(SqlValue.Text("MANAGED COMPILED VDBE"));
    }

    [Test]
    public void WrappedExtremumAggregateControlsTheRepresentativeRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(label TEXT, value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES ('middle', 5), ('low', 1), ('high', 9);");

        ReadRows(connection, "SELECT label, abs(max(value)) FROM t;")[0]
            .Should().Equal(SqlValue.Text("high"), SqlValue.Integer(9));
        ReadRows(connection, "SELECT label, coalesce(min(value), max(value)) FROM t;")[0]
            .Should().Equal(SqlValue.Text("high"), SqlValue.Integer(1));
        ReadRows(connection, "SELECT label, NOT max(value) FROM t;")[0]
            .Should().Equal(SqlValue.Text("high"), SqlValue.Integer(0));
    }

    private static List<(SqlValue, SqlValue, SqlValue)> DrainGrouped(EmbeddedStatement statement)
    {
        var rows = new List<(SqlValue, SqlValue, SqlValue)>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add((statement.GetValue(0), statement.GetValue(1), statement.GetValue(2)));

        return rows;
    }

    private static List<SqlValue[]> DrainRows(EmbeddedStatement statement)
    {
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

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static void AssertMatchesSqlite(
        IReadOnlyList<string> setup,
        string query,
        params SqlValue[] parameters)
    {
        using var managed = OpenManaged(setup);
        var managedRows = ReadRows(managed, query, parameters);

        using var sqlite = OpenSqlite(setup);
        using var command = sqlite.CreateCommand();
        command.CommandText = query;
        for (var index = 0; index < parameters.Length; index++)
            command.Parameters.AddWithValue($"?{index + 1}", ToSqliteValue(parameters[index]));

        using var reader = command.ExecuteReader();
        var sqliteRows = new List<SqlValue[]>();
        while (reader.Read())
        {
            var row = new SqlValue[reader.FieldCount];
            for (var column = 0; column < row.Length; column++)
                row[column] = FromSqliteValue(reader.IsDBNull(column) ? null : reader.GetValue(column));
            sqliteRows.Add(row);
        }

        managedRows.Should().HaveCount(sqliteRows.Count);
        for (var index = 0; index < managedRows.Count; index++)
            managedRows[index].Should().Equal(sqliteRows[index]);
    }

    private static EmbeddedConnection OpenManaged(IReadOnlyList<string> setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        return connection;
    }

    private static MsData.SqliteConnection OpenSqlite(IReadOnlyList<string> setup)
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static object ToSqliteValue(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => DBNull.Value,
        SqlValueKind.Integer => value.AsInteger(),
        SqlValueKind.Real => value.AsReal(),
        SqlValueKind.Text => value.AsText(),
        SqlValueKind.Blob => value.AsBlob().ToArray(),
        _ => throw new InvalidOperationException($"Unsupported SQLite parameter type {value.Kind}."),
    };

    private static SqlValue FromSqliteValue(object? value) => value switch
    {
        null => SqlValue.Null,
        long integer => SqlValue.Integer(integer),
        double real => SqlValue.Real(real),
        string text => SqlValue.Text(text),
        byte[] blob => SqlValue.Blob(blob),
        _ => throw new InvalidOperationException($"Unsupported SQLite value type {value.GetType()}."),
    };

    private static List<SqlValue[]> ReadRows(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

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
