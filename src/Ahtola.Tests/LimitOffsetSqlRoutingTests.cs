using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes the supported LIMIT/OFFSET SQL subset through the real
// OffsetGate/LimitGate opcodes layered onto a gate-able base program by LimitOffsetProgramBuilder,
// and that the routed results stay byte-identical to the tree-walking evaluator. EXPLAIN is the
// ground truth for "was this lowered to bytecode?": a routed statement dumps the gate opcodes,
// while every deliberate fallback shape throws because EXPLAIN only describes lowered programs.
//
// Routable subset (the base with LIMIT/OFFSET stripped must itself lower): a direct single-table
// scan, a source-less constant or scalar-function projection, scalar/grouped aggregates with an
// aggregate-only HAVING predicate, or a bounded single-table sorter over bare-column/literal
// projections and resolved column ORDER BY keys. Deliberate fallbacks keep the evaluator's exact
// rows AND error timing: LIMIT 0 (validate-then-skip-the-scan), scan scalar functions (which may
// throw on a row a gate would not reach), non-simple/outer JOIN + LIMIT, DISTINCT + LIMIT,
// compound + LIMIT, unsupported ORDER BY shapes, and non-integer LIMIT/OFFSET ("datatype mismatch").
public class LimitOffsetSqlRoutingTests
{
    // ---- Scan + LIMIT / OFFSET routing ------------------------------------------------------

    [Test]
    public void ScanLimitRoutesThroughTheLimitGate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30), (40), (50);");

        ReadRows(connection, "SELECT value FROM t LIMIT 2;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(10), SqlValue.Integer(20));

        var program = ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 2;");
        Opcodes(program).Should().Equal(
            "LoadConstant", "OpenReadCursor", "Rewind", "Column", "LimitGate", "ResultRow", "Next",
            "CloseCursor", "Halt");

        // The prologue seeds the limit counter, and the gate stops the stream at the terminating Halt.
        program[0][6].Should().Be(SqlValue.Text("r[1]=2"));
        program[4][6].Should().Be(SqlValue.Text("goto 8 when r[1]<=0, else decrement r[1]"));
    }

    [Test]
    public void ScanLimitOffsetRoutesThroughBothGates()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30), (40), (50);");

        ReadRows(connection, "SELECT value FROM t LIMIT 2 OFFSET 1;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(20), SqlValue.Integer(30));

        var program = ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 2 OFFSET 1;");
        Opcodes(program).Should().Equal(
            "LoadConstant", "LoadConstant", "OpenReadCursor", "Rewind", "Column", "OffsetGate",
            "LimitGate", "ResultRow", "Next", "CloseCursor", "Halt");

        // OFFSET counter is seeded first (stable register layout), LIMIT counter second.
        program[0][6].Should().Be(SqlValue.Text("r[1]=1"));
        program[1][6].Should().Be(SqlValue.Text("r[2]=2"));

        // OFFSET skips to the loop-advance without charging LIMIT; LIMIT stops at the Halt.
        program[5][6].Should().Be(SqlValue.Text("goto 8 and decrement r[1] while r[1]>0"));
        program[6][6].Should().Be(SqlValue.Text("goto 10 when r[2]<=0, else decrement r[2]"));
    }

    [Test]
    public void UnboundedLimitWithOffsetRoutesThroughTheOffsetGateOnly()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30), (40), (50);");

        // LIMIT -1 is unbounded, so only the OFFSET gate is emitted.
        ReadRows(connection, "SELECT value FROM t LIMIT -1 OFFSET 2;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(30), SqlValue.Integer(40), SqlValue.Integer(50));

        var program = ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT -1 OFFSET 2;");
        Opcodes(program).Should().Equal(
            "LoadConstant", "OpenReadCursor", "Rewind", "Column", "OffsetGate", "ResultRow", "Next",
            "CloseCursor", "Halt");
        Opcodes(program).Should().NotContain("LimitGate");
        program[0][6].Should().Be(SqlValue.Text("r[1]=2"));
        program[4][6].Should().Be(SqlValue.Text("goto 6 and decrement r[1] while r[1]>0"));
    }

    [Test]
    public void CommaLimitFormAppliesOffsetThenLimit()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30), (40), (50);");

        // "LIMIT x, y" binds x as OFFSET and y as LIMIT, matching the OFFSET-then-LIMIT window.
        ReadRows(connection, "SELECT value FROM t LIMIT 1, 2;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(20), SqlValue.Integer(30));

        var comma = ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 1, 2;");
        var explicitForm = ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 2 OFFSET 1;");
        Opcodes(comma).Should().Equal(Opcodes(explicitForm));
    }

    [Test]
    public void ScanOffsetBeyondRowCountRoutesAndReturnsEmpty()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        ReadRows(connection, "SELECT value FROM t LIMIT 5 OFFSET 100;").Should().BeEmpty();

        // Still routes (the empty result is produced entirely inside the gated program).
        Opcodes(ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 5 OFFSET 100;"))
            .Should().Contain("OffsetGate").And.Contain("LimitGate");
    }

    [Test]
    public void ScanLimitLargerThanRowCountReturnsAllRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        ReadRows(connection, "SELECT value FROM t LIMIT 100;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    // ---- Negative / integral-real bounds ----------------------------------------------------

    [Test]
    public void NegativeLimitIsUnboundedAndRoutesAsAPlainScan()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        ReadRows(connection, "SELECT value FROM t LIMIT -1;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));

        // A negative (unbounded) limit with no offset needs no gate, so the builder returns the
        // base program unchanged: the EXPLAIN is byte-identical to the un-limited scan.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT -1;")).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "ResultRow", "Next", "CloseCursor", "Halt");
    }

    [Test]
    public void NegativeOffsetClampsToZeroAndDropsTheOffsetGate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        // A negative offset skips nothing, so it clamps to zero and only the LIMIT gate remains.
        ReadRows(connection, "SELECT value FROM t LIMIT 2 OFFSET -5;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 2 OFFSET -5;")).ToList();
        opcodes.Should().Contain("LimitGate");
        opcodes.Should().NotContain("OffsetGate");
    }

    [Test]
    public void IntegralRealLimitIsAcceptedAndRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        // A real that is exactly integral resolves like the integer, matching RequireLimitInteger.
        ReadRows(connection, "SELECT value FROM t LIMIT 1.0;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 1.0;")).Should().Contain("LimitGate");
    }

    // ---- Constant projection ----------------------------------------------------------------

    [Test]
    public void ConstantProjectionLimitRoutesThroughTheGate()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRows(connection, "SELECT 42 LIMIT 5;")[0][0].Should().Be(SqlValue.Integer(42));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT 42 LIMIT 5;")).Should().Equal(
            "LoadConstant", "LoadConstant", "LimitGate", "ResultRow", "Halt");
    }

    [Test]
    public void ConstantProjectionOffsetSkipsTheSoleRow()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The single constant candidate is offset away, so nothing is emitted.
        ReadRows(connection, "SELECT 42 LIMIT 1 OFFSET 1;").Should().BeEmpty();
        Opcodes(ReadRows(connection, "EXPLAIN SELECT 42 LIMIT 1 OFFSET 1;"))
            .Should().Contain("OffsetGate").And.Contain("LimitGate");
    }

    // ---- Source-less scalar Function ---------------------------------------------------------

    [Test]
    public void SourceLessScalarFunctionLimitOffsetRoutesAndMatchesSqlite()
    {
        const string query = "SELECT upper(@value) LIMIT @limit OFFSET @offset;";
        var bindings = new[]
        {
            SqlValue.Text("mIxEd"),
            SqlValue.Integer(1),
            SqlValue.Integer(0),
        };

        using var connection = new EmbeddedDatabase().Connect();
        var managed = ReadRows(connection, query, bindings);
        managed.Should().ContainSingle().Which.Should().Equal(SqlValue.Text("MIXED"));
        AssertMatchesSqlite(
            managed,
            query,
            ("@value", "mIxEd"),
            ("@limit", 1L),
            ("@offset", 0L));

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN " + query, bindings)).ToList();
        opcodes.Should().Contain("Function").And.Contain("LimitGate");
        opcodes.Should().NotContain("OffsetGate");

        var skipped = ReadRows(
            connection,
            query,
            SqlValue.Text("mIxEd"),
            SqlValue.Integer(1),
            SqlValue.Integer(1));
        skipped.Should().BeEmpty();
        Opcodes(ReadRows(
                connection,
                "EXPLAIN " + query,
                SqlValue.Text("mIxEd"),
                SqlValue.Integer(1),
                SqlValue.Integer(1)))
            .Should().Contain("Function").And.Contain("OffsetGate").And.Contain("LimitGate");
    }

    // ---- Aggregate --------------------------------------------------------------------------

    [Test]
    public void ScalarAggregateLimitRoutesAlongsideTheAccumulator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30);");

        ReadRows(connection, "SELECT count(*) FROM t LIMIT 5;")[0][0].Should().Be(SqlValue.Integer(3));

        // The whole statement (accumulator + gate) is lowered together.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*) FROM t LIMIT 5;"))
            .Should().Contain("AggReset").And.Contain("AggStep").And.Contain("AggFinalize")
            .And.Contain("LimitGate");
    }

    [Test]
    public void ScalarAggregateOffsetSkipsTheSoleAggregateRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30);");

        // A scalar aggregate emits exactly one row, so OFFSET 1 empties the result.
        ReadRows(connection, "SELECT count(*) FROM t LIMIT 5 OFFSET 1;").Should().BeEmpty();
        Opcodes(ReadRows(connection, "EXPLAIN SELECT count(*) FROM t LIMIT 5 OFFSET 1;"))
            .Should().Contain("OffsetGate").And.Contain("LimitGate");
    }

    [Test]
    public void GroupedAggregateLimitRoutesAndKeepsSortedKeyGroups()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2, 20), (1, 10), (2, 5), (1, 7);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k LIMIT 1;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT k, count(*) FROM t GROUP BY k LIMIT 1;"))
            .Should().Contain("SameGroup").And.Contain("AggStep").And.Contain("LimitGate");
    }

    [Test]
    public void GroupedAggregateOffsetRoutesAndSkipsLeadingGroups()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(k INTEGER, v INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2, 20), (1, 10), (2, 5), (1, 7);");

        var rows = ReadRows(connection, "SELECT k, count(*) FROM t GROUP BY k LIMIT 1 OFFSET 1;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT k, count(*) FROM t GROUP BY k LIMIT 1 OFFSET 1;"))
            .Should().Contain("OffsetGate").And.Contain("LimitGate");
    }

    // ---- Parameters + reset lifecycle -------------------------------------------------------

    [Test]
    public void ParameterisedLimitRoutesWithTheBoundValue()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4);");

        using var statement = connection.Prepare("SELECT value FROM t LIMIT ?1;");
        statement.Bind(1, SqlValue.Integer(2));

        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void ParameterisedOffsetAndLimitRouteWithBoundValues()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4), (5);");

        using var statement = connection.Prepare("SELECT value FROM t LIMIT ?1 OFFSET ?2;");
        statement.Bind(1, SqlValue.Integer(2));
        statement.Bind(2, SqlValue.Integer(1));

        Drain(statement).Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void ResetAndRebindRecompilesTheGateWithTheNewParameter()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4);");

        using var statement = connection.Prepare("SELECT value FROM t LIMIT ?1;");
        statement.Bind(1, SqlValue.Integer(1));
        Drain(statement).Should().Equal(SqlValue.Integer(1));

        // The statement recompiles per execution, so a reset + rebind bakes the new bound LIMIT.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(3));
        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void ResetReplaysTheGatedProgramAgainstLiveRows()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        using var statement = connection.Prepare("SELECT value FROM t LIMIT 10;");
        Drain(statement).Should().Equal(SqlValue.Integer(1));

        Execute(connection, "INSERT INTO t VALUES (2), (3);");

        statement.Reset();
        Drain(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    // ---- Columns / metadata -----------------------------------------------------------------

    [Test]
    public void LimitPreservesProjectionColumnNames()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'ada'), (2, 'grace');");

        ColumnNames(connection, "SELECT name AS label, id FROM t LIMIT 1;")
            .Should().Equal("label", "id");

        var rows = ReadRows(connection, "SELECT name AS label, id FROM t LIMIT 1;");
        rows.Should().ContainSingle();
        rows[0].Should().Equal(SqlValue.Text("ada"), SqlValue.Integer(1));
    }

    [Test]
    public void RoutedWindowMatchesTheEvaluatorForEveryOffsetLimitPair()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20), (30), (40), (50);");
        var all = new[] { 10, 20, 30, 40, 50 };

        for (var offset = 0; offset <= 6; offset++)
        {
            for (var limit = 0; limit <= 6; limit++)
            {
                var sql = $"SELECT value FROM t LIMIT {limit} OFFSET {offset};";
                var expected = all.Skip(offset).Take(limit).Select(v => SqlValue.Integer(v)).ToArray();
                ReadRows(connection, sql).Select(row => row[0]).Should().Equal(expected, $"for {sql}");
            }
        }
    }

    // ---- Fallback boundaries (evaluator keeps the exact rows and error timing) --------------

    [Test]
    public void LimitZeroFallsBackToTheEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        // LIMIT 0 validates then skips the scan on the evaluator, so it is not routed.
        ReadRows(connection, "SELECT value FROM t LIMIT 0;").Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 0;"));
    }

    [Test]
    public void LimitZeroStillValidatesProjectionColumnsOnTheEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // The evaluator validates every projection against a synthetic row even when it returns
        // no rows, so an unknown column still raises. Routing a gate would scan first and miss this.
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT missing FROM t LIMIT 0;"));
    }

    [Test]
    public void BoundedScalarFunctionScanFallsBackToTheEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (-3), (-2);");

        // A gate could halt before a later scalar call runs; keep scans with Function opcodes
        // evaluator-owned so their projection error timing cannot change.
        ReadRows(connection, "SELECT abs(x) FROM t LIMIT 1;")[0]
            .Should().Equal(SqlValue.Integer(3));
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT abs(x) FROM t LIMIT 1;"));
    }

    [Test]
    public void SourceLessScalarFunctionLimitZeroFallsBackToTheEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();
        const string query = "SELECT upper(@value) LIMIT 0;";

        ReadRows(connection, query, SqlValue.Text("mIxEd")).Should().BeEmpty();
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN " + query, SqlValue.Text("mIxEd")));
    }

    [Test]
    public void SimpleOrderByLimitRoutesThroughSorterAndLimitGate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (3), (1), (2);");

        ReadRows(connection, "SELECT value FROM t ORDER BY value LIMIT 2;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT value FROM t ORDER BY value LIMIT 2;"))
            .Should().Contain("OpenSorter").And.Contain("SorterInsert").And.Contain("SorterSort")
            .And.Contain("LimitGate");
    }

    [Test]
    public void SimpleInnerEquiJoinLimitRoutesThroughTheGate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(x INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1), (2), (3);");
        Execute(connection, "INSERT INTO b VALUES (1), (2), (3);");

        ReadRows(connection, "SELECT a.x FROM a JOIN b ON a.x = b.x LIMIT 2;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT a.x FROM a JOIN b ON a.x = b.x LIMIT 2;"))
            .Should().Contain("FilterRegisters").And.Contain("LimitGate");
    }

    [Test]
    public void DistinctLimitNowLowersThroughARowGate()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (1), (2), (2), (3);");

        ReadRows(connection, "SELECT DISTINCT value FROM t LIMIT 2;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        // De-duplication used to be fused into DistinctResultRow, which the gate could not sit in
        // front of. It is now a standalone RowGate so duplicates never charge against the limit.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT DISTINCT value FROM t LIMIT 2;"))
            .Should().ContainInOrder("RowGate", "LimitGate", "ResultRow");
    }

    [Test]
    public void CompoundLimitNowLowersToBytecode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // The parser attaches LIMIT to the compound; the gate is now applied to the assembled
        // compound program rather than refusing it.
        ReadRows(connection, "SELECT 1 UNION ALL SELECT 2 LIMIT 1;")
            .Select(row => row[0]).Should().Equal(SqlValue.Integer(1));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT 1 UNION ALL SELECT 2 LIMIT 1;"))
            .Should().ContainInOrder("LimitGate", "ResultRow");
    }

    [Test]
    public void TextLimitFallsBackAndRaisesDatatypeMismatch()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        // A non-integer LIMIT must raise on the evaluator at its exact pre-scan point.
        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT value FROM t LIMIT 'x';"))!;
        error.Message.Should().Be("datatype mismatch");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 'x';"));
    }

    [Test]
    public void NullLimitFallsBackAndRaisesDatatypeMismatch()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT value FROM t LIMIT NULL;"))!;
        error.Message.Should().Be("datatype mismatch");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT NULL;"));
    }

    [Test]
    public void TextOffsetFallsBackAndRaisesDatatypeMismatch()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        var error = Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "SELECT value FROM t LIMIT 1 OFFSET 'x';"))!;
        error.Message.Should().Be("datatype mismatch");
        Assert.Throws<EmbeddedSqlException>(
            () => ReadRows(connection, "EXPLAIN SELECT value FROM t LIMIT 1 OFFSET 'x';"));
    }

    private static List<SqlValue> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(statement.GetValue(0));

        return rows;
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

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

    private static void AssertMatchesSqlite(
        IReadOnlyList<SqlValue[]> managed,
        string query,
        params (string Name, object Value)[] parameters)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = query;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        using var reader = command.ExecuteReader();
        var row = 0;
        while (reader.Read())
        {
            managed.Should().HaveCountGreaterThan(row);
            managed[row].Should().HaveCount(reader.FieldCount);
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var reference = reader.IsDBNull(column) ? null : reader.GetValue(column);
                CellsShouldMatch(managed[row][column], reference);
            }

            row++;
        }

        managed.Should().HaveCount(row);
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference)
    {
        switch (reference)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null);
                break;
            case long integer:
                managed.Should().Be(SqlValue.Integer(integer));
                break;
            case string text:
                managed.Should().Be(SqlValue.Text(text));
                break;
            default:
                Assert.Fail($"Unexpected SQLite value type {reference.GetType().Name}.");
                break;
        }
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
