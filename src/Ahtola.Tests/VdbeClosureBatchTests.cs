using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Closure coverage for the VDBE/compiler batch that lowered recursive-CTE outer LIMIT pushdown,
// control-flow RETURNING expressions, and LIMIT/OFFSET composition over conditional row emitters.
// Every test is an independent observable closure: either a value the engine previously could not
// produce, or an EXPLAIN shape proving the statement is now bytecode rather than evaluator-owned.
public class VdbeClosureBatchTests
{
    // ---- 1. recursive CTE outer LIMIT pushdown / early stop --------------------------------------

    [Test]
    public void OuterLimitOverANonTerminatingRecursiveCteStopsEarlyInsteadOfOverflowing()
    {
        using var connection = Connect();

        // Before pushdown the CTE materialized to its row ceiling and threw before the outer LIMIT
        // could ever trim it. The budget now stops the expansion at exactly the observable prefix.
        Values(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT x FROM c LIMIT 5;")
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4),
                SqlValue.Integer(5));
    }

    [Test]
    public void OuterLimitOffsetOverANonTerminatingRecursiveCteBudgetsBothBounds()
    {
        using var connection = Connect();

        // The budget is limit + offset, so the skipped prefix is still produced and the window lands
        // on the same rows the evaluator would have trimmed from a full materialization.
        Values(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT x FROM c LIMIT 3 OFFSET 4;")
            .Should().Equal(SqlValue.Integer(5), SqlValue.Integer(6), SqlValue.Integer(7));

        // UNION (de-duplicating) recursion takes the same budget.
        Values(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION SELECT x + 1 FROM c) SELECT x FROM c LIMIT 4;")
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(2),
                SqlValue.Integer(3),
                SqlValue.Integer(4));

        // A recursion that terminates on its own is unaffected: a larger LIMIT still yields every row.
        Values(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 6) SELECT x FROM c LIMIT 10;")
            .Should().HaveCount(6);
    }

    [Test]
    public void OuterLimitPushdownIsRefusedWhenTheCteHasASecondConsumer()
    {
        using var connection = Connect();

        // A truncated materialization would leak into the join's other side, so the shape check
        // declines and the full (bounded) expansion still applies.
        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            connection,
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) "
            + "SELECT c.x FROM c, c AS d LIMIT 2;"))!;
        error.Message.Should().Contain("exceeded the maximum");
    }

    // ---- 2. the 100,000-row cap no longer bounds legitimate finite recursion ---------------------

    [Test]
    public void FiniteRecursionFarAboveTheOldHundredThousandRowCapCompletes()
    {
        using var connection = Connect();

        // 150,000 rows: rejected outright by the previous 100,000-row cap, now materializes normally.
        Values(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 150000) SELECT count(*) FROM c;")
            .Should().Equal(SqlValue.Integer(150000));

        Values(connection, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 120000) SELECT max(x) FROM c;")
            .Should().Equal(SqlValue.Integer(120000));
    }

    [Test]
    public void UnboundedRecursionStillFailsLoudlyAtTheMemoryBackstop()
    {
        using var connection = Connect();

        // The nontermination mechanism is retained; only its value moved, and it now reports the
        // memory backstop rather than the old query-shape cap.
        var error = Assert.Throws<EmbeddedSqlException>(() => ReadRows(
            connection,
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT * FROM c;"))!;
        error.Message.Should().Be("recursive query for c exceeded the maximum of 1000000 rows");
    }

    // ---- 3. compiled DML RETURNING searched CASE -------------------------------------------------

    [Test]
    public void InsertReturningSearchedCaseCompilesToBranchOpcodes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (5) RETURNING CASE WHEN value > 3 THEN 'big' ELSE 'small' END;"))
            .Should().Equal(
                "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor", "Rewind", "Column",
                "LoadConstant", "Compare", "JumpIfNotTrue", "LoadConstant", "Goto", "LoadConstant",
                "ResultRow", "Next", "CloseCursor", "Commit", "CloseCursor", "Halt");

        RoutedValue(connection, "INSERT INTO t VALUES (5) RETURNING CASE WHEN value > 3 THEN 'big' ELSE 'small' END;")
            .Should().Be(SqlValue.Text("big"));

        // The relocated ELSE arm is reached for the complementary predicate.
        RoutedValue(connection, "INSERT INTO t VALUES (1) RETURNING CASE WHEN value > 3 THEN 'big' ELSE 'small' END;")
            .Should().Be(SqlValue.Text("small"));
    }

    // ---- 4. RETURNING AND ------------------------------------------------------------------------

    [Test]
    public void UpdateReturningAndCompilesToShortCircuitJumps()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (4);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN UPDATE t SET value = 5 RETURNING value > 1 AND value < 9;")).ToList();
        opcodes.Should().Contain("JumpIf");
        opcodes.Should().Contain("Goto");

        RoutedValue(connection, "UPDATE t SET value = 6 RETURNING value > 1 AND value < 9;")
            .Should().Be(SqlValue.Integer(1));

        // Short circuit: the left operand alone decides a false result.
        RoutedValue(connection, "UPDATE t SET value = 0 RETURNING value > 1 AND value < 9;")
            .Should().Be(SqlValue.Integer(0));
    }

    // ---- 5. RETURNING OR -------------------------------------------------------------------------

    [Test]
    public void InsertReturningOrCompilesToShortCircuitJumps()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (4) RETURNING value < 1 OR value > 3;"))
            .Should().Contain("JumpIf");

        RoutedValue(connection, "INSERT INTO t VALUES (4) RETURNING value < 1 OR value > 3;")
            .Should().Be(SqlValue.Integer(1));
        RoutedValue(connection, "INSERT INTO t VALUES (2) RETURNING value < 1 OR value > 3;")
            .Should().Be(SqlValue.Integer(0));
    }

    // ---- 6. RETURNING IN-list --------------------------------------------------------------------

    [Test]
    public void InsertReturningInListCompilesToRepeatedCompareJumps()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (2) RETURNING value IN (1, 2, 3);")).ToList();
        // One Compare/JumpIf pair per list element, all funnelling into the single emitted register.
        opcodes.Count(opcode => opcode == "Compare").Should().Be(3);
        opcodes.Count(opcode => opcode == "JumpIf").Should().Be(3);

        RoutedValue(connection, "INSERT INTO t VALUES (2) RETURNING value IN (1, 2, 3);")
            .Should().Be(SqlValue.Integer(1));
        RoutedValue(connection, "INSERT INTO t VALUES (9) RETURNING value IN (1, 2, 3);")
            .Should().Be(SqlValue.Integer(0));
    }

    // ---- 7. RETURNING NOT IN-list ----------------------------------------------------------------

    [Test]
    public void DeleteReturningNotInListCompilesAndNegatesTheMembershipResult()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (9);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN DELETE FROM t RETURNING value NOT IN (1, 2);")).ToList();
        opcodes.Count(opcode => opcode == "Compare").Should().Be(2);
        // The trailing Function is the negation applied to the membership result.
        opcodes.Should().Contain("Function");

        RoutedValue(connection, "DELETE FROM t RETURNING value NOT IN (1, 2);")
            .Should().Be(SqlValue.Integer(1));

        Execute(connection, "INSERT INTO t VALUES (1);");
        RoutedValue(connection, "DELETE FROM t RETURNING value NOT IN (1, 2);")
            .Should().Be(SqlValue.Integer(0));
    }

    // ---- 8. RETURNING nested AND/OR --------------------------------------------------------------

    [Test]
    public void InsertReturningNestedAndOrCompilesWithCorrectlyRelocatedJumps()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        const string sql = "INSERT INTO t VALUES (?1) RETURNING (value > 1 AND value < 9) OR value = 100;";
        Opcodes(ExplainBound(connection, "EXPLAIN " + sql, SqlValue.Integer(5)))
            .Should().Contain("JumpIf");

        // All three branch outcomes of the nested tree are exercised, proving the relocated targets
        // land on the right arms rather than merely being in range.
        Bound(connection, sql, SqlValue.Integer(5)).Should().Be(SqlValue.Integer(1));
        Bound(connection, sql, SqlValue.Integer(100)).Should().Be(SqlValue.Integer(1));
        Bound(connection, sql, SqlValue.Integer(0)).Should().Be(SqlValue.Integer(0));
    }

    // ---- 9. RETURNING CASE containing IN ---------------------------------------------------------

    [Test]
    public void InsertReturningCaseOverAnInListCompilesBothNestedControlFlowForms()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        const string sql = "INSERT INTO t VALUES (?1) RETURNING CASE WHEN value IN (1, 2) THEN 1 ELSE 0 END;";
        var opcodes = Opcodes(ExplainBound(connection, "EXPLAIN " + sql, SqlValue.Integer(1))).ToList();
        opcodes.Should().Contain("JumpIf");
        opcodes.Should().Contain("JumpIfNotTrue");
        opcodes.Should().Contain("Goto");

        Bound(connection, sql, SqlValue.Integer(1)).Should().Be(SqlValue.Integer(1));
        Bound(connection, sql, SqlValue.Integer(7)).Should().Be(SqlValue.Integer(0));
    }

    // ---- 10. no evaluator fallback for control-flow RETURNING ------------------------------------

    [Test]
    public void EveryControlFlowReturningShapeLowersToTheTwoCursorCompiledProgram()
    {
        foreach (var returning in new[]
                 {
                     "CASE WHEN value > 3 THEN 'big' ELSE 'small' END",
                     "value > 1 AND value < 9",
                     "value < 1 OR value > 3",
                     "value IN (1, 2, 3)",
                     "value NOT IN (1, 2)",
                     "(value > 1 AND value < 9) OR value = 100",
                     "CASE WHEN value IN (1, 2) THEN 1 ELSE 0 END",
                 })
        {
            using var connection = Connect();
            Execute(connection, "CREATE TABLE t(value INTEGER);");

            // EXPLAIN succeeding at all is the proof: the evaluator fallback raises
            // "EXPLAIN is only supported for statements lowered to the bytecode compiler."
            var opcodes = Opcodes(ReadRows(connection, $"EXPLAIN INSERT INTO t VALUES (5) RETURNING {returning};")).ToList();

            // The compiled RETURNING program is the two-phase shape: mutate under the write cursor,
            // then re-scan the same rows under a read cursor to project the expression.
            opcodes.Should().StartWith(new[] { "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor" });
            opcodes.Should().EndWith(new[] { "ResultRow", "Next", "CloseCursor", "Commit", "CloseCursor", "Halt" });
            opcodes.Count(opcode => opcode == "OpenWriteCursor").Should().Be(1);
            opcodes.Count(opcode => opcode == "OpenReadCursor").Should().Be(1);
        }
    }

    // ---- 11. LIMIT/OFFSET composability with the row-set (compound set-operation) family ---------

    [Test]
    public void IntersectWithLimitOffsetLowersThroughTheRowSetInstructionFamily()
    {
        using var connection = SetOperationFixture();

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT x FROM a INTERSECT SELECT x FROM b LIMIT 1 OFFSET 1;")).ToList();

        // Both terms still populate probe sets, and the output pass now runs
        // RowGate -> OffsetGate -> LimitGate -> ResultRow so a probed-and-rejected candidate never
        // reaches the counters.
        opcodes.Count(opcode => opcode == "RowSetInsert").Should().Be(2);
        opcodes.Should().ContainInOrder("RowSetRewind", "RowGate", "OffsetGate", "LimitGate", "ResultRow", "RowSetNext");
        opcodes.Should().NotContain("CompoundResultRow");

        Values(connection, "SELECT x FROM a INTERSECT SELECT x FROM b LIMIT 2;")
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
        Values(connection, "SELECT x FROM a INTERSECT SELECT x FROM b LIMIT 1 OFFSET 1;")
            .Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void ExceptAndUnionWithLimitOffsetLowerAndKeepEvaluatorSemantics()
    {
        using var connection = SetOperationFixture();

        Opcodes(ReadRows(connection, "EXPLAIN SELECT x FROM a EXCEPT SELECT x FROM b LIMIT 1 OFFSET 1;"))
            .Should().ContainInOrder("RowGate", "OffsetGate", "LimitGate", "ResultRow");
        Values(connection, "SELECT x FROM a EXCEPT SELECT x FROM b LIMIT 1 OFFSET 1;")
            .Should().Equal(SqlValue.Integer(4));

        // UNION DISTINCT keeps its de-duplication guard ahead of the counter.
        Opcodes(ReadRows(connection, "EXPLAIN SELECT x FROM a UNION SELECT x FROM b LIMIT 3;"))
            .Should().Contain("LimitGate");
        Values(connection, "SELECT x FROM a UNION SELECT x FROM b LIMIT 3;")
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Integer(3));

        // UNION ALL splices per-term ResultRows that share one counter pair, so the window spans the
        // concatenated stream.
        Values(connection, "SELECT x FROM a UNION ALL SELECT x FROM b LIMIT 3 OFFSET 4;")
            .Should().Equal(SqlValue.Integer(5), SqlValue.Integer(2), SqlValue.Integer(3));
    }

    // ---- 12. DISTINCT aggregates and LIMIT/OFFSET over DISTINCT emitters -------------------------

    [Test]
    public void ThreeIndependentDistinctAggregatesShareOneScanAndFinalizeSeparately()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER, d INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1,1,1),(1,2,2),(2,2,2),(2,2,3);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT count(DISTINCT a), sum(DISTINCT b), count(DISTINCT d) FROM t;")).ToList();
        opcodes.Count(opcode => opcode == "AggFinalize").Should().Be(3);
        opcodes.Count(opcode => opcode == "Rewind").Should().Be(1);

        ReadRows(connection, "SELECT count(DISTINCT a), sum(DISTINCT b), count(DISTINCT d) FROM t;")
            .Single()
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3), SqlValue.Integer(3));
    }

    [Test]
    public void DirectDistinctComposesWithLimitOffsetThroughARowGate()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1),(1),(2),(2),(3);");

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT DISTINCT a FROM t LIMIT 1 OFFSET 1;")).ToList();

        // DistinctResultRow folded de-duplication into the emit opcode, so it could not be gated
        // without charging duplicates against the bounds. It is now split apart.
        opcodes.Should().NotContain("DistinctResultRow");
        opcodes.Should().ContainInOrder("RowGate", "OffsetGate", "LimitGate", "ResultRow");

        // OFFSET counts distinct rows, not scanned rows: the second *distinct* value is returned.
        Values(connection, "SELECT DISTINCT a FROM t LIMIT 1 OFFSET 1;").Should().Equal(SqlValue.Integer(2));
        Values(connection, "SELECT DISTINCT a FROM t LIMIT 2;")
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
        Values(connection, "SELECT DISTINCT a FROM t LIMIT 10 OFFSET 1;")
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
        Values(connection, "SELECT DISTINCT a FROM t LIMIT 0;").Should().BeEmpty();

        // Aggregate DISTINCT under GROUP BY keeps composing as before.
        using var grouped = Connect();
        Execute(grouped, "CREATE TABLE g(a INTEGER, b INTEGER);");
        Execute(grouped, "INSERT INTO g VALUES (1,1),(1,2),(2,2),(2,2);");
        ReadRows(grouped, "SELECT a, count(DISTINCT b) FROM g GROUP BY a LIMIT 1 OFFSET 1;")
            .Single()
            .Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static EmbeddedConnection Connect() => new EmbeddedDatabase().Connect();

    private static EmbeddedConnection SetOperationFixture()
    {
        var connection = Connect();
        Execute(connection, "CREATE TABLE a(x INTEGER);");
        Execute(connection, "CREATE TABLE b(x INTEGER);");
        Execute(connection, "INSERT INTO a VALUES (1),(2),(3),(4),(5);");
        Execute(connection, "INSERT INTO b VALUES (2),(3),(5);");
        return connection;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue RoutedValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static SqlValue Bound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static List<SqlValue[]> ExplainBound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        return DrainRows(statement);
    }

    private static List<SqlValue> Values(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Select(row => row[0]).ToList();

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return DrainRows(statement);
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
}
