using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Routing coverage for arithmetic RETURNING expressions. The DML compiler reuses the generic SELECT
// emitter, including its late-bound parameters, numeric affinity, nested arithmetic, and conservative
// builtin-function gate. Unsupported semantic families remain evaluator-owned.
public class DmlReturningArithmeticSqlRoutingTests
{
    // ---- routed opcode proofs ------------------------------------------------------------------

    [Test]
    public void InsertReturningColumnArithmeticRoutesToArithmeticOpcode()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor", "Rewind", "Column",
            "LoadConstant", "NumericAffinity", "NumericAffinity", "Arithmetic", "ResultRow", "Next",
            "CloseCursor", "Commit", "CloseCursor", "Halt");

        // The column reads into a scratch register, the literal bakes to another, and the real
        // Arithmetic opcode folds the operand block into the output register the ResultRow emits.
        Comments(rows).Should().Contain("r[1]=c1.col[0]");
        Comments(rows).Should().Contain("r[2]=1");
        Comments(rows).Should().Contain("r[0]=r[1] + r[2]");
        Comments(rows).Should().Contain("output=r[0]");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void AllFiveArithmeticOperatorsRouteForInsertReturning()
    {
        foreach (var (op, expected) in new[]
                 {
                     ("+", SqlValue.Integer(13)),
                     ("-", SqlValue.Integer(7)),
                     ("*", SqlValue.Integer(30)),
                     // Integer division truncates toward zero, exactly as the evaluator does.
                     ("/", SqlValue.Integer(3)),
                     ("%", SqlValue.Integer(1)),
                 })
        {
            using var connection = Connect();
            Execute(connection, "CREATE TABLE t(value INTEGER);");

            Opcodes(ReadRows(connection, $"EXPLAIN INSERT INTO t VALUES (10) RETURNING value {op} 3;"))
                .Should().Contain("Arithmetic");
            RoutedValue(connection, $"INSERT INTO t VALUES (10) RETURNING value {op} 3;")
                .Should().Be(expected);
        }
    }

    [Test]
    public void InsertReturningRowidArithmeticRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING rowid + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor", "Rewind", "RowId",
            "LoadConstant", "NumericAffinity", "NumericAffinity", "Arithmetic", "ResultRow", "Next",
            "CloseCursor", "Commit", "CloseCursor", "Halt");

        // The rowid pseudo-column is always an integer, so it feeds arithmetic through the dedicated
        // RowId opcode regardless of any declared column affinity.
        Comments(rows).Should().Contain("r[1]=c1.rowid");

        // The first auto-assigned rowid is 1, so the routed fold returns 2.
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING rowid + 1;")
            .Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void InsertReturningNestedArithmeticRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A nested arithmetic operand recurses through the same lowering, emitting one Arithmetic
        // opcode per operation over its own scratch operand block.
        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING (value + 1) * 2;");
        Opcodes(rows).Count(opcode => opcode == "Arithmetic").Should().Be(2);
        Opcodes(rows).Should().Contain("Arithmetic");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING (value + 1) * 2;")
            .Should().Be(SqlValue.Integer(22));
    }

    [Test]
    public void InsertReturningParameterOperandRoutesWhenBoundNumeric()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A parameter bound to an integer bakes to a LoadConstant, so the fold routes.
        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + ?;", SqlValue.Integer(5)))
            .Should().Contain("Arithmetic");

        using var statement = connection.Prepare("INSERT INTO t VALUES (10) RETURNING value + ?;");
        statement.Bind(1, SqlValue.Integer(5));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(15));
        statement.Step().Should().Be(StatementStepResult.Done);

        // Each Step recompiles and re-bakes the parameter, so a reset with a fresh binding routes
        // the new value.
        statement.Reset();
        statement.Bind(1, SqlValue.Integer(100));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(110));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RealColumnArithmeticRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value REAL);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (2.5) RETURNING value * 2;"))
            .Should().Contain("Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (2.5) RETURNING value * 2;")
            .Should().Be(SqlValue.Real(5.0));
    }

    [Test]
    public void NumericAffinityColumnArithmeticRoutes()
    {
        using var connection = Connect();

        // NUMERIC affinity is a numeric (non-text, non-blob) affinity, so it is part of the routable
        // subset alongside INTEGER and REAL.
        Execute(connection, "CREATE TABLE t(value NUMERIC);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + 1;"))
            .Should().Contain("Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void UpdateReturningColumnArithmeticRoutesAndObservesPostWriteRow()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10);");

        var rows = ReadRows(connection, "EXPLAIN UPDATE t SET value = 20 RETURNING value + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Update", "Next", "OpenReadCursor", "Rewind", "Column",
            "LoadConstant", "NumericAffinity", "NumericAffinity", "Arithmetic", "ResultRow", "Next",
            "CloseCursor", "Commit", "CloseCursor", "Halt");

        // UPDATE RETURNING projects the post-write row, so value + 1 folds over the new 20.
        RoutedValue(connection, "UPDATE t SET value = 20 RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(21));
    }

    [Test]
    public void DeleteReturningColumnArithmeticRoutesAndObservesPreDeleteRow()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10);");

        var rows = ReadRows(connection, "EXPLAIN DELETE FROM t RETURNING value + 1;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Delete", "Next", "OpenReadCursor", "Rewind", "Column",
            "LoadConstant", "NumericAffinity", "NumericAffinity", "Arithmetic", "ResultRow", "Next",
            "CloseCursor", "Commit", "CloseCursor", "Halt");

        // DELETE RETURNING projects the pre-delete row, so value + 1 folds over the removed 10.
        RoutedValue(connection, "DELETE FROM t RETURNING value + 1;")
            .Should().Be(SqlValue.Integer(11));
        ReadRows(connection, "SELECT value FROM t;").Should().BeEmpty();
    }

    [Test]
    public void PureConstantArithmeticFoldsWithoutArithmeticOpcode()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // Arithmetic over only constants is constant-folded by the constant-projection route ahead of
        // this one, so it bakes a single LoadConstant and emits no Arithmetic opcode.
        var rows = ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING 1 + 2;");
        Opcodes(rows).Should().Equal(
            "OpenWriteCursor", "Rewind", "Insert", "Next", "OpenReadCursor", "Rewind",
            "LoadConstant", "ResultRow", "Next", "CloseCursor", "Commit", "CloseCursor", "Halt");
        Opcodes(rows).Should().NotContain("Arithmetic");
        Comments(rows).Should().Contain("r[0]=3");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING 1 + 2;")
            .Should().Be(SqlValue.Integer(3));
    }

    // ---- evaluator fallbacks -------------------------------------------------------------------

    [Test]
    public void TextAffinityColumnOperandRoutesThroughNumericAffinity()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(label TEXT);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES ('10') RETURNING label + 1;"))
            .Should().ContainInOrder("Column", "NumericAffinity", "Arithmetic");

        RoutedValue(connection, "INSERT INTO t VALUES ('10') RETURNING label + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void BlobAffinityColumnOperandRoutesThroughNumericAffinity()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(data BLOB);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (x'0102') RETURNING data + 1;"))
            .Should().ContainInOrder("Column", "NumericAffinity", "Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (x'0102') RETURNING data + 1;")
            .Should().Be(SqlValue.Integer(1));
    }

    [Test]
    public void AllowListedFunctionOperandRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (-4) RETURNING abs(value) + 1;"))
            .Should().ContainInOrder("Function", "NumericAffinity", "Arithmetic");

        RoutedValue(connection, "INSERT INTO t VALUES (-4) RETURNING abs(value) + 1;")
            .Should().Be(SqlValue.Integer(5));
    }

    [Test]
    public void SubqueryOperandFallsBackToEvaluator()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // A scalar subquery operand is outside the leaf subset, so the whole projection declines.
        ExplainRefused(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + (SELECT 1);");
    }

    [Test]
    public void ValueOnlyCollationOperandRoutes()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value COLLATE NOCASE + 1;"))
            .Should().Contain("Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value COLLATE NOCASE + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void CastOperandRoutesThroughTheCastOpcode()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        // CAST lowers to a typed Cast instruction, so it is a valid arithmetic operand.
        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING CAST(value AS INTEGER) + 1;"))
            .Should().ContainInOrder("Cast", "Arithmetic");
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING CAST(value AS INTEGER) + 1;")
            .Should().Be(SqlValue.Integer(11));
    }

    [Test]
    public void ComparisonProjectionUsesCompiledLowering()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value < 5;"))
            .Should().Contain("Compare");

        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value < 5;")
            .Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void ConcatenationProjectionUsesCompiledLowering()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ReadRows(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value || 'x';"))
            .Should().Contain("Function");
        RoutedValue(connection, "INSERT INTO t VALUES (10) RETURNING value || 'x';")
            .Should().Be(SqlValue.Text("10x"));
    }

    [Test]
    public void BareParameterProjectionRoutesWithLateBinding()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING ?;", SqlValue.Integer(7)))
            .Should().Contain("LoadParameter");
        using var statement = connection.Prepare("INSERT INTO t VALUES (10) RETURNING ?;");
        statement.Bind(1, SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(7));
    }

    [Test]
    public void ParameterOperandRebindToTextKeepsTheCompiledShape()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + ?;", SqlValue.Integer(5)))
            .Should().Contain("Arithmetic");
        Opcodes(ExplainBound(connection, "EXPLAIN INSERT INTO t VALUES (10) RETURNING value + ?;", SqlValue.Text("x")))
            .Should().ContainInOrder("LoadParameter", "NumericAffinity", "Arithmetic");

        // Executing with the text binding still succeeds through the evaluator, which applies numeric
        // affinity to the unparseable text operand (treated as 0).
        using var statement = connection.Prepare("INSERT INTO t VALUES (10) RETURNING value + ?;");
        statement.Bind(1, SqlValue.Text("x"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(10));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void RoutedFunctionErrorPropagatesBeforeCommit()
    {
        using var connection = Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        Execute(connection, "INSERT INTO t VALUES (7);");

        using (var statement = connection.Prepare(
                   "UPDATE t SET value = -9223372036854775808 RETURNING abs(value);"))
        {
            Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
                .Message.Should().Be("integer overflow");
        }

        var rows = ReadRows(connection, "SELECT value FROM t;");
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(7));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static EmbeddedConnection Connect() => new EmbeddedDatabase().Connect();

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

    private static void ExplainRefused(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static List<SqlValue[]> ExplainBound(EmbeddedConnection connection, string sql, params SqlValue[] positional)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < positional.Length; index++)
            statement.Bind(index + 1, positional[index]);

        return DrainRows(statement);
    }

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

    private static IEnumerable<string> Comments(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[6].AsText());
}
