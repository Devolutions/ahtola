using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Proves that EmbeddedDatabase routes the supported builtin scalar-function SQL subset through the real
// Function opcode (ScalarFunctionProgramBuilder.BuildOverValues / BuildOverScan) and that the routed
// results, NULLs, and errors stay byte-identical to the tree-walking evaluator, which the routed delegate
// reuses verbatim. Two source shapes lower:
//   * source-less: SELECT f(<literal|parameter>, ...)      -> Function over a baked argument row
//   * single scan: SELECT <col>, ..., f(<col>, ...) FROM t -> Function per scanned row (function emitted last)
// Only the allow-list { abs, coalesce, hex, ifnull, length, lower, typeof, upper } routes -- every one reads
// only its evaluated argument values, so applying it through the opcode over the same values is exact.
// As in the sibling routing suites, EXPLAIN is the ground truth for "was this lowered to bytecode?": a routed
// statement dumps its opcode stream (including Function), while every deliberate fallback shape throws because
// EXPLAIN only describes lowered programs. Fallback tests also assert the evaluator still produces the correct
// value or error. Parameter arguments are baked as constants at compile time (the generic SELECT execution
// path supplies no parameter binding); because each Step recompiles, a rebind re-bakes the fresh value.
public class ScalarFunctionSqlRoutingTests
{
    // ---- source-less "values" route: routed values, NULLs, opcodes -------------------------------------

    [Test]
    public void ScalarFunctionOverParameterRoutesToFunctionOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("SELECT abs(?)");
        statement.Bind(1, SqlValue.Integer(-5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Done);

        // The parameter remains late-bound and feeds the real Function opcode.
        var rows = ExplainBound(connection, "EXPLAIN SELECT abs(?)", SqlValue.Integer(-5));
        Opcodes(rows).Should().Equal("LoadParameter", "Function", "ResultRow", "Halt");
        Comments(rows).Should().Contain("r[0]=abs(r[1])");
    }

    [Test]
    public void ScalarFunctionArgumentNullPropagatesThroughOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("SELECT abs(?)");
        statement.Bind(1, SqlValue.Null);
        statement.Step().Should().Be(StatementStepResult.Row);

        // abs(NULL) is NULL, computed by the evaluator's own helper reached through the opcode delegate.
        statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        statement.Step().Should().Be(StatementStepResult.Done);

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT abs(?)", SqlValue.Null)).Should().Contain("Function");
    }

    [Test]
    public void VariadicCoalesceRoutesAndReturnsFirstNonNull()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("SELECT coalesce(?, ?, ?)");
        statement.Bind(1, SqlValue.Null);
        statement.Bind(2, SqlValue.Text("second"));
        statement.Bind(3, SqlValue.Text("third"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("second"));
        statement.Step().Should().Be(StatementStepResult.Done);

        // A variadic builtin routes with three late-bound argument registers feeding one Function opcode.
        var rows = ExplainBound(
            connection, "EXPLAIN SELECT coalesce(?, ?, ?)", SqlValue.Null, SqlValue.Null, SqlValue.Null);
        Opcodes(rows).Count(opcode => opcode == "LoadParameter").Should().Be(3);
        Opcodes(rows).Count(opcode => opcode == "Function").Should().Be(1);
        Comments(rows).Should().Contain("r[0]=coalesce(r[1..3])");
    }

    [Test]
    public void HexTypeofLengthLowerUpperIfnullAllRouteAndMatchEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        SingleRoutedValue(connection, "SELECT hex(?)", SqlValue.Text("A")).Should().Be(SqlValue.Text("41"));
        SingleRoutedValue(connection, "SELECT typeof(?)", SqlValue.Real(1.5)).Should().Be(SqlValue.Text("real"));
        SingleRoutedValue(connection, "SELECT length(?)", SqlValue.Text("héllo"))
            .Should().Be(SqlValue.Integer(5));
        SingleRoutedValue(connection, "SELECT lower(?)", SqlValue.Text("MiXeD"))
            .Should().Be(SqlValue.Text("mixed"));
        SingleRoutedValue(connection, "SELECT upper(?)", SqlValue.Text("MiXeD"))
            .Should().Be(SqlValue.Text("MIXED"));

        using var ifnull = connection.Prepare("SELECT ifnull(?, ?)");
        ifnull.Bind(1, SqlValue.Null);
        ifnull.Bind(2, SqlValue.Integer(99));
        ifnull.Step().Should().Be(StatementStepResult.Row);
        ifnull.GetValue(0).Should().Be(SqlValue.Integer(99));

        // Each of these lowered (their EXPLAIN describes a Function program).
        foreach (var sql in new[]
                 {
                     "EXPLAIN SELECT hex(?)", "EXPLAIN SELECT typeof(?)", "EXPLAIN SELECT length(?)",
                     "EXPLAIN SELECT lower(?)", "EXPLAIN SELECT upper(?)",
                 })
        {
            Opcodes(ExplainBound(connection, sql, SqlValue.Null)).Should().Contain("Function");
        }
    }

    [Test]
    public void MixedLiteralAndParameterArgumentsRouteOverValues()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // coalesce over a parameter plus a bare literal is still a values row of Literal/Parameter cells.
        using var statement = connection.Prepare("SELECT coalesce(?, 0)");
        statement.Bind(1, SqlValue.Null);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(0));

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT coalesce(?, 0)", SqlValue.Null)).Should().Contain("Function");
    }

    [Test]
    public void BlobArgumentRoundTripsThroughHexOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("SELECT hex(?)");
        statement.Bind(1, SqlValue.Blob(new byte[] { 0x01, 0x02, 0xFF }));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("0102FF"));
    }

    // ---- parameter baking: rebind + program-shape independence -----------------------------------------

    [Test]
    public void RebindAcrossResetReflectsFreshlyBakedArgument()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("SELECT abs(?)");

        statement.Bind(1, SqlValue.Integer(-5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Reset then rebind the same slot: the routed program recompiles per execution, re-baking the new
        // argument, so it yields the freshly bound result.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(-9));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(9));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void LateBoundArgumentKeepsExplainShapeIndependentOfValue()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var withSeven = Dump(ExplainBound(connection, "EXPLAIN SELECT abs(?)", SqlValue.Integer(-7)));
        var withNine = Dump(ExplainBound(connection, "EXPLAIN SELECT abs(?)", SqlValue.Integer(-9)));

        withSeven.Should().Equal(withNine);
        withSeven.Should().Contain(entry => entry.Contains("LoadParameter") && entry.Contains("param[0]"));
    }

    // ---- error propagation (routed): byte-identical evaluator diagnostics ------------------------------

    [Test]
    public void OverflowErrorPropagatesUnwrappedFromOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using var statement = connection.Prepare("SELECT abs(?)");
        statement.Bind(1, SqlValue.Integer(long.MinValue));
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("integer overflow");
    }

    [Test]
    public void ArityErrorIsRaisedByEvaluatorAtExecutionNotTheBuilder()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A fixed-arity builtin called with the wrong count still routes (the delegate is variadic), so the
        // arity error is raised by the evaluator at execution with its exact message -- not pre-rejected.
        using (var statement = connection.Prepare("SELECT abs(?, ?)"))
        {
            statement.Bind(1, SqlValue.Integer(1));
            statement.Bind(2, SqlValue.Integer(2));
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("wrong number of arguments to function abs()");
        }

        // EXPLAIN still describes the lowered program: describing never invokes the delegate, so no throw.
        Opcodes(ExplainBound(connection, "EXPLAIN SELECT abs(?, ?)", SqlValue.Null, SqlValue.Null))
            .Should().Contain("Function");
    }

    // ---- single-table "scan" route: column arguments, passthrough, NULLs -------------------------------

    [Test]
    public void ScalarFunctionOverColumnRoutesToScanFunctionOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (-3), (4), (NULL);");

        ReadRows(connection, "SELECT abs(x) FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4), SqlValue.Null);

        // The scan lowers to a real cursor loop whose per-row Function reads the argument column.
        var rows = ReadRows(connection, "EXPLAIN SELECT abs(x) FROM t;");
        Opcodes(rows).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "Function", "ResultRow", "Next", "CloseCursor", "Halt");
        Comments(rows).Should().Contain("r[0]=abs(r[1])").And.Contain("output=r[0]");
    }

    [Test]
    public void PassthroughColumnsPrecedeTheFunctionResult()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'ab'), (2, 'CDE');");

        var rows = ReadRows(connection, "SELECT id, upper(name) FROM t;");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Text("AB"));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("CDE"));

        // Passthrough column fills r[0]; the function result lands last in r[1]; both are emitted in order.
        var explain = ReadRows(connection, "EXPLAIN SELECT id, upper(name) FROM t;");
        Opcodes(explain).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "Column", "Function", "ResultRow", "Next", "CloseCursor", "Halt");
        Comments(explain).Should().Contain("r[1]=upper(r[2])").And.Contain("output=r[0..1]");
    }

    [Test]
    public void MultiArgumentScalarOverColumnsRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(a INTEGER, b INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (NULL, 7), (3, 9);");

        ReadRows(connection, "SELECT ifnull(a, b) FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(7), SqlValue.Integer(3));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT ifnull(a, b) FROM t;")).Should().Contain("Function");
    }

    [Test]
    public void ScanFunctionOverEmptyTableProducesNoRowsAndNoError()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");

        // No rows means the per-row Function never runs, so even a would-throw argument raises nothing.
        ReadRows(connection, "SELECT abs(x) FROM t;").Should().BeEmpty();
        Opcodes(ReadRows(connection, "EXPLAIN SELECT abs(x) FROM t;")).Should().Contain("Function");
    }

    // ---- row-independent calls still execute at their normal row position --------------------------------

    [Test]
    public void PureConstantCallExecutesThroughFunctionOpcode()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRows(connection, "SELECT abs(-5);")[0][0].Should().Be(SqlValue.Integer(5));

        var opcodes = Opcodes(ReadRows(connection, "EXPLAIN SELECT abs(-5);")).ToList();
        opcodes.Should().Contain("LoadConstant").And.Contain("Function");
    }

    [Test]
    public void ConstantArgumentOverScanExecutesFunctionPerRow()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        ReadRows(connection, "SELECT abs(5) FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(5), SqlValue.Integer(5));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT abs(5) FROM t;")).Should().Contain("Function");
    }

    // ---- fallback boundaries: evaluator keeps ownership, EXPLAIN refuses to describe -------------------

    [Test]
    public void NestedFunctionArgumentRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using (var statement = connection.Prepare("SELECT abs(abs(?))"))
        {
            statement.Bind(1, SqlValue.Integer(-5));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        }

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT abs(abs(?))", SqlValue.Integer(-5)))
            .Count(opcode => opcode == "Function").Should().Be(2);
    }

    [Test]
    public void ArithmeticArgumentRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using (var statement = connection.Prepare("SELECT abs(? + 1)"))
        {
            statement.Bind(1, SqlValue.Integer(-6));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        }

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT abs(? + 1)", SqlValue.Integer(-6)))
            .Should().Contain("Arithmetic").And.Contain("Function");
    }

    [Test]
    public void CollationSensitiveNullifFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // nullif reads the argument AST for its collation, so it is deliberately excluded; the evaluator
        // still returns the correct value.
        using (var statement = connection.Prepare("SELECT nullif(?, ?)"))
        {
            statement.Bind(1, SqlValue.Integer(3));
            statement.Bind(2, SqlValue.Integer(3));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        }

        ExplainRefused(connection, "EXPLAIN SELECT nullif(?, ?)", SqlValue.Integer(3), SqlValue.Integer(3));
    }

    [Test]
    public void JsonFunctionFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // json_* functions inspect the argument AST for error text, so they are excluded wholesale.
        using (var statement = connection.Prepare("SELECT json_array(?)"))
        {
            statement.Bind(1, SqlValue.Integer(1));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Text("[1]"));
        }

        ExplainRefused(connection, "EXPLAIN SELECT json_array(?)", SqlValue.Integer(1));
    }

    [Test]
    public void RowIdFilterOnScanFunctionRoutes()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (-3), (4), (-1);");

        ReadRows(connection, "SELECT abs(x) FROM t WHERE rowid = 1;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(3));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT abs(x) FROM t WHERE rowid = 1;"))
            .Should().Contain("SeekRowid").And.Contain("Function");
    }

    [Test]
    public void FunctionMayAppearAnywhereInProjectionOrder()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1, -3);");

        ReadRows(connection, "SELECT abs(x), id FROM t;")[0]
            .Should().Equal(SqlValue.Integer(3), SqlValue.Integer(1));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT abs(x), id FROM t;"))
            .Should().Contain("Function");
    }

    [Test]
    public void MixedColumnAndLiteralArgumentsOverScanRoute()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE t(x INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (NULL), (5);");

        ReadRows(connection, "SELECT coalesce(x, 0) FROM t;").Select(row => row[0])
            .Should().Equal(SqlValue.Integer(0), SqlValue.Integer(5));

        Opcodes(ReadRows(connection, "EXPLAIN SELECT coalesce(x, 0) FROM t;"))
            .Should().Contain("Function");
    }

    [Test]
    public void DistinctFallsBackToEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        using (var statement = connection.Prepare("SELECT DISTINCT abs(?)"))
        {
            statement.Bind(1, SqlValue.Integer(-5));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Integer(5));
        }

        ExplainRefused(connection, "EXPLAIN SELECT DISTINCT abs(?)", SqlValue.Integer(-5));
    }

    [Test]
    public void UserDefinedFunctionShadowKeepsTheNameOnTheEvaluator()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A UDF registered under a builtin name (matching the call's arity) shadows the builtin in the
        // evaluator's own dispatch, so the router must decline to keep the UDF authoritative.
        connection.RegisterScalarFunction("abs", 1, _ => SqlValue.Text("shadowed"));

        using (var statement = connection.Prepare("SELECT abs(?)"))
        {
            statement.Bind(1, SqlValue.Integer(-5));
            statement.Step().Should().Be(StatementStepResult.Row);
            statement.GetValue(0).Should().Be(SqlValue.Text("shadowed"));
        }

        ExplainRefused(connection, "EXPLAIN SELECT abs(?)", SqlValue.Integer(-5));
    }

    [Test]
    public void UnrelatedArityUserFunctionDoesNotBlockRouting()
    {
        using var connection = new EmbeddedDatabase().Connect();

        // A UDF registered under a builtin name but a different arity does not shadow the 1-argument call, so
        // the builtin still routes to the Function opcode -- mirroring the evaluator's arity-keyed dispatch.
        connection.RegisterScalarFunction("abs", 2, _ => SqlValue.Text("two-arg"));

        using var statement = connection.Prepare("SELECT abs(?)");
        statement.Bind(1, SqlValue.Integer(-5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(5));

        Opcodes(ExplainBound(connection, "EXPLAIN SELECT abs(?)", SqlValue.Integer(-5))).Should().Contain("Function");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static SqlValue SingleRoutedValue(EmbeddedConnection connection, string sql, SqlValue argument)
    {
        using var statement = connection.Prepare(sql);
        statement.Bind(1, argument);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static void ExplainRefused(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());

    private static List<string> Dump(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => $"{row[1].AsText()}|{row[6].AsText()}").ToList();

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        return DrainRows(statement);
    }

    // Prepares an EXPLAIN statement, binds the given values positionally (EXPLAIN still requires every
    // parameter bound before it can describe the program), and reads its opcode rows.
    private static List<SqlValue[]> ExplainBound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

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
}
