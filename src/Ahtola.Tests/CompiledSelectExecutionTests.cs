using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class CompiledSelectExecutionTests
{
    [Test]
    public void ConstantSelectExecutesThroughTheCompiledProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 1, 2 + 3 * 4, 'a' || 'b';");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Integer(14));
        statement.GetValue(2).Should().Be(SqlValue.Text("ab"));
        statement.Step().Should().Be(StatementStepResult.Done);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void CompiledSelectExposesProjectionColumnNames()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 1 AS a, 2 + 3 AS b;");

        statement.GetColumnCount().Should().Be(2);
        statement.GetColumnName(0).Should().Be("a");
        statement.GetColumnName(1).Should().Be("b");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(1).Should().Be(SqlValue.Integer(5));
    }

    [Test]
    public void CompiledSelectSupportsResetAndReplaysTheProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT 42;");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(42));
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.Reset();
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(42));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void CompiledSelectFoldsScalarFunctionsAndBlobConcatenation()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT abs(-2), upper('ada'), length('Ada'), x'01' || x'02';");

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(2));
        statement.GetValue(1).Should().Be(SqlValue.Text("ADA"));
        statement.GetValue(2).Should().Be(SqlValue.Integer(3));
        statement.GetValue(3).Should().Be(SqlValue.Text("\u0001\u0002"));
    }

    [Test]
    public void ExplainDumpsTheRealCompiledProgram()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var columns = ColumnNames(connection, "EXPLAIN SELECT 10, 20;");
        columns.Should().Equal("addr", "opcode", "p1", "p2", "p3", "p4", "comment");

        var rows = ReadRows(connection, "EXPLAIN SELECT 10, 20;");
        Opcodes(rows).Should().Equal("LoadConstant", "LoadConstant", "ResultRow", "Halt");

        // addr column counts up from zero.
        for (var index = 0; index < rows.Count; index++)
            rows[index][0].Should().Be(SqlValue.Integer(index));

        // First LoadConstant targets register 0 and carries the folded literal.
        rows[0][2].Should().Be(SqlValue.Integer(0));
        rows[0][5].Should().Be(SqlValue.Text("10"));
        rows[0][6].Should().Be(SqlValue.Text("r[0]=10"));

        // Second LoadConstant targets register 1.
        rows[1][2].Should().Be(SqlValue.Integer(1));
        rows[1][5].Should().Be(SqlValue.Text("20"));

        // ResultRow spans both registers (start register 0, count 2).
        rows[2][2].Should().Be(SqlValue.Integer(0));
        rows[2][3].Should().Be(SqlValue.Integer(2));
        rows[2][6].Should().Be(SqlValue.Text("output=r[0..1]"));

        // Halt carries no operands.
        rows[3][5].Should().Be(SqlValue.Null);
        rows[3][6].Should().Be(SqlValue.Text("halt"));
    }

    [Test]
    public void ExplainFoldsExpressionsBeforeEmittingConstants()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var rows = ReadRows(connection, "EXPLAIN SELECT 2 + 3 * 4, 9 / 3;");
        Opcodes(rows).Should().Equal("LoadConstant", "LoadConstant", "ResultRow", "Halt");
        rows[0][5].Should().Be(SqlValue.Text("14"));
        rows[1][5].Should().Be(SqlValue.Text("3"));
    }

    [Test]
    public void ExplainSupportsSortedScansAndRejectsUnsupportedShapes()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (2), (1);");

        var sortedRows = ReadRows(connection, "SELECT value FROM t ORDER BY value;");
        sortedRows.Select(row => row[0]).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));

        var program = ReadRows(connection, "EXPLAIN SELECT value FROM t ORDER BY value;");
        Opcodes(program).Should().Equal(
            "OpenReadCursor", "OpenSorter", "Rewind", "Column", "RowId", "SorterInsert", "Next",
            "CloseCursor", "SorterSort", "SorterData", "Copy", "ResultRow", "SorterNext",
            "CloseSorter", "Halt");

        Opcodes(ReadRows(connection, "EXPLAIN SELECT value + 1 FROM t;"))
            .Should().ContainInOrder("Column", "LoadConstant", "NumericAffinity", "NumericAffinity", "Arithmetic");
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN SELECT DISTINCT 1;"));
        Opcodes(ReadRows(connection, "EXPLAIN SELECT value FROM t ORDER BY value LIMIT 1;"))
            .Should().Contain("SorterSort").And.Contain("LimitGate");
        // A DELETE whose WHERE embeds a subquery cannot run against a single scanned row.
        Assert.Throws<EmbeddedSqlException>(() => ReadRows(connection, "EXPLAIN DELETE FROM t WHERE value IN (SELECT 1);"));
    }

    [Test]
    public void ExplainDescribesLateBoundSelectParameters()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("EXPLAIN SELECT ?1;");
        statement.Bind(1, SqlValue.Integer(5));

        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetValue).ToArray());

        Opcodes(rows).Should().Equal("LoadParameter", "ResultRow", "Halt");
        rows[0][6].Should().Be(SqlValue.Text("r[0]=param[0]"));
    }

    [Test]
    public void ExplainQueryPlanUsesTheSqliteColumnShape()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ColumnNames(connection, "EXPLAIN QUERY PLAN SELECT 1;")
            .Should().Equal("id", "parent", "notused", "detail");
    }

    [Test]
    public void CompiledSelectPreservesFunctionResolutionErrors()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var error = Assert.Throws<EmbeddedSqlException>(
            () => connection.Prepare("SELECT no_such_function(1);").Step())!;
        error.Message.Should().Contain("no such function");
    }

    [Test]
    public void EvaluatorStillHandlesStatementsOutsideTheSubset()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        // ORDER BY is outside the scan subset but still returns rows via the evaluator.
        ReadRows(connection, "SELECT value FROM t ORDER BY value DESC;").Should().HaveCount(3);

        // Clauses outside the subset (LIMIT here) keep the evaluator semantics.
        ReadRows(connection, "SELECT 1 LIMIT 0;").Should().BeEmpty();

        // DISTINCT is outside the subset but still executes through the evaluator.
        ReadRows(connection, "SELECT DISTINCT 7;").Should().ContainSingle();
    }

    [Test]
    public void ExplainDumpsScanProgramForSingleTableProjection()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN SELECT value FROM t;");
        Opcodes(rows).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "ResultRow", "Next", "CloseCursor", "Halt");

        // OpenReadCursor names the table and reports its column count.
        rows[0][2].Should().Be(SqlValue.Integer(0));
        rows[0][4].Should().Be(SqlValue.Integer(1));
        rows[0][5].Should().Be(SqlValue.Text("t"));
        rows[0][6].Should().Be(SqlValue.Text("open read cursor 0 on t (1 cols)"));

        // Rewind jumps past the loop to CloseCursor when the table is empty.
        rows[1][3].Should().Be(SqlValue.Integer(5));

        // Column copies cursor column 0 into register 0.
        rows[2][6].Should().Be(SqlValue.Text("r[0]=c0.col[0]"));

        // Next loops back to the top of the body (address 2).
        rows[4][3].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void ExplainDumpsScanProgramWithNativeComparisonPredicate()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        var rows = ReadRows(connection, "EXPLAIN SELECT value FROM t WHERE value > 1;");
        Opcodes(rows).Should().Equal(
            "OpenReadCursor", "Rewind", "Column", "LoadConstant", "Compare", "JumpIfNotTrue",
            "Column", "ResultRow", "Next", "CloseCursor", "Halt");

        // The native comparison writes a three-valued result, and the branch skips NULL and false rows.
        rows[4][5].Should().Be(SqlValue.Text("GreaterThan"));
        rows[5][3].Should().Be(SqlValue.Integer(8));
        rows[5][6].Should().Be(SqlValue.Text("goto 8 if r[1] is not true"));
    }

    [Test]
    public void CompiledScanProjectsColumnsInOrder()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'ada'), (2, 'grace');");

        using var statement = connection.Prepare("SELECT name, id FROM t;");
        statement.GetColumnName(0).Should().Be("name");
        statement.GetColumnName(1).Should().Be("id");

        var rows = ReadRows(connection, "SELECT name, id FROM t;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Text("ada"));
        rows[0][1].Should().Be(SqlValue.Integer(1));
        rows[1][0].Should().Be(SqlValue.Text("grace"));
        rows[1][1].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void CompiledScanExpandsStarToEveryColumn()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, name TEXT);");
        Execute(connection, "INSERT INTO t VALUES (7, 'ada');");

        var rows = ReadRows(connection, "SELECT * FROM t;");
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(7));
        rows[0][1].Should().Be(SqlValue.Text("ada"));
    }

    [Test]
    public void CompiledScanMixesColumnsAndConstants()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (10), (20);");

        var rows = ReadRows(connection, "SELECT 1, value FROM t;");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Should().Be(SqlValue.Integer(10));
        rows[1][0].Should().Be(SqlValue.Integer(1));
        rows[1][1].Should().Be(SqlValue.Integer(20));
    }

    [Test]
    public void CompiledScanFiltersRowsWithWhere()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3), (4);");

        var rows = ReadRows(connection, "SELECT value FROM t WHERE value >= 3;");
        rows.Select(row => row[0]).Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
    }

    [Test]
    public void CompiledScanOverEmptyTableReturnsNoRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");

        ReadRows(connection, "SELECT value FROM t;").Should().BeEmpty();
        ReadRows(connection, "SELECT value FROM t WHERE value > 0;").Should().BeEmpty();
    }

    [Test]
    public void CompiledScanResolvesQualifiedColumnsThroughAlias()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (5), (6);");

        var rows = ReadRows(connection, "SELECT x.value FROM t AS x WHERE x.value > 5;");
        rows.Should().ContainSingle();
        rows[0][0].Should().Be(SqlValue.Integer(6));
    }

    [Test]
    public void CompiledScanSupportsResetAndReplaysLiveRows()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1);");

        using var statement = connection.Prepare("SELECT value FROM t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.Step().Should().Be(StatementStepResult.Done);

        Execute(connection, "INSERT INTO t VALUES (2);");

        statement.Reset();
        var replayed = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            replayed.Add(statement.GetValue(0));

        replayed.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void CompiledScanMatchesEvaluatorForParameterisedPredicate()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2), (3);");

        using var statement = connection.Prepare("SELECT value FROM t WHERE value >= ?1;");
        statement.Bind(1, SqlValue.Integer(2));

        var rows = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(statement.GetValue(0));

        rows.Should().Equal(SqlValue.Integer(2), SqlValue.Integer(3));
    }

    [Test]
    public void CompiledCastsPreserveSqliteConversionAndParameterResetBehavior()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT CAST(?1 AS INTEGER), CAST(?2 AS REAL), CAST(?3 AS TEXT), CAST(?4 AS BLOB);");
        statement.Bind(1, SqlValue.Text("42.9"));
        statement.Bind(2, SqlValue.Text("3"));
        statement.Bind(3, SqlValue.Integer(7));
        statement.Bind(4, SqlValue.Text("ab"));

        statement.Step().Should().Be(StatementStepResult.Row);
        ReadCurrentRow(statement).Should().Equal(
            SqlValue.Integer(42),
            SqlValue.Real(3),
            SqlValue.Text("7"),
            SqlValue.Blob("ab"u8.ToArray()));
        statement.Step().Should().Be(StatementStepResult.Done);

        statement.Reset();
        statement.Bind(1, SqlValue.Text("-2"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(-2));

        Execute(connection, "CREATE TABLE t(value TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('10'), ('20');");
        Opcodes(ReadRows(connection, "EXPLAIN SELECT CAST(value AS INTEGER) FROM t;"))
            .Should()
            .ContainInOrder("Column", "Cast", "ResultRow");
        ReadRows(connection, "SELECT CAST(value AS INTEGER) FROM t;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(10), SqlValue.Integer(20));
    }

    [Test]
    public void CompiledSearchedCasePreservesBranchOrderAndLazyErrors()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT CASE WHEN ?1 THEN 'first' WHEN ?2 THEN 'second' ELSE 'last' END;");
        statement.Bind(1, SqlValue.Integer(0));
        statement.Bind(2, SqlValue.Integer(1));
        DrainValues(statement).Should().Equal(SqlValue.Text("second"));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(1));
        statement.Bind(2, SqlValue.Integer(1));
        DrainValues(statement).Should().Equal(SqlValue.Text("first"));

        using var explain = connection.Prepare(
            "EXPLAIN SELECT CASE WHEN ?1 THEN 'first' WHEN ?2 THEN 'second' ELSE 'last' END;");
        explain.Bind(1, SqlValue.Integer(0));
        explain.Bind(2, SqlValue.Integer(1));
        var explainOpcodes = new List<string>();
        while (explain.Step() == StatementStepResult.Row)
            explainOpcodes.Add(explain.GetValue(1).AsText());
        explainOpcodes
            .Should()
            .ContainInOrder("LoadParameter", "JumpIfNotTrue", "LoadConstant", "Goto")
            .And
            .Contain("ResultRow");

        using var lazyError = connection.Prepare(
            "SELECT CASE WHEN ?1 THEN abs(-9223372036854775808) ELSE 7 END;");
        lazyError.Bind(1, SqlValue.Integer(0));
        DrainValues(lazyError).Should().Equal(SqlValue.Integer(7));
        lazyError.Reset();
        lazyError.Bind(1, SqlValue.Integer(1));
        Assert.Throws<EmbeddedSqlException>(() => lazyError.Step())!
            .Message
            .Should()
            .Be("integer overflow");

        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (0), (1);");
        ReadRows(
                connection,
                "SELECT CASE WHEN value THEN 'yes' ELSE 'no' END FROM t WHERE value >= 0;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Text("no"), SqlValue.Text("yes"));

        using var simpleCase = connection.Prepare(
            "SELECT CASE ?1 WHEN ?2 THEN 'match' ELSE 'miss' END;");
        simpleCase.Bind(1, SqlValue.Integer(2));
        simpleCase.Bind(2, SqlValue.Integer(2));
        DrainValues(simpleCase).Should().Equal(SqlValue.Text("match"));
        simpleCase.Reset();
        simpleCase.Bind(1, SqlValue.Null);
        simpleCase.Bind(2, SqlValue.Null);
        DrainValues(simpleCase).Should().Equal(SqlValue.Text("miss"));

        using var limitedCase = connection.Prepare(
            "SELECT CASE WHEN ?1 THEN 'limited' ELSE 'miss' END LIMIT 1;");
        limitedCase.Bind(1, SqlValue.Integer(1));
        DrainValues(limitedCase).Should().Equal(SqlValue.Text("limited"));

        using var compoundCasts = connection.Prepare(
            "SELECT CAST(?1 AS INTEGER) UNION ALL SELECT CAST(?2 AS INTEGER);");
        compoundCasts.Bind(1, SqlValue.Text("4"));
        compoundCasts.Bind(2, SqlValue.Text("5"));
        DrainValues(compoundCasts).Should().Equal(SqlValue.Integer(4), SqlValue.Integer(5));
    }

    [Test]
    public void NativePredicatesMatchSqliteAffinityNullAndBuiltinCollationRules()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            """
            CREATE TABLE t(
                i INTEGER,
                r REAL,
                binary_value TEXT COLLATE BINARY,
                nocase_value TEXT COLLATE NOCASE,
                rtrim_value TEXT COLLATE RTRIM
            );
            """);
        Execute(
            connection,
            """
            INSERT INTO t VALUES
                (2, 2.5, 'Ada', 'Ada', 'Ada '),
                (3, 3.5, 'ada', 'ada', 'Ada'),
                (NULL, NULL, NULL, NULL, NULL);
            """);

        using var parameterized = connection.Prepare("SELECT i FROM t WHERE i = ?1;");
        parameterized.Bind(1, SqlValue.Text("2"));
        DrainValues(parameterized).Should().Equal(SqlValue.Integer(2));
        parameterized.Reset();
        parameterized.Bind(1, SqlValue.Integer(3));
        DrainValues(parameterized).Should().Equal(SqlValue.Integer(3));

        ReadRows(connection, "SELECT i FROM t WHERE r > '3';")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(3));
        ReadRows(connection, "SELECT i FROM t WHERE binary_value = 'Ada';")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(2));
        ReadRows(connection, "SELECT i FROM t WHERE nocase_value = 'ADA';")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(2), SqlValue.Integer(3));
        ReadRows(connection, "SELECT i FROM t WHERE rtrim_value = 'Ada';")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(2), SqlValue.Integer(3));
        ReadRows(connection, "SELECT i FROM t WHERE i IS NULL;")
            .Should()
            .ContainSingle()
            .Which[0]
            .Should()
            .Be(SqlValue.Null);
        ReadRows(connection, "SELECT i FROM t WHERE i IS NOT NULL;")
            .Select(row => row[0])
            .Should()
            .Equal(SqlValue.Integer(2), SqlValue.Integer(3));
        ReadRows(connection, "SELECT i FROM t WHERE i = NULL;").Should().BeEmpty();
    }

    [Test]
    public void OverriddenBuiltinCollationKeepsPredicatesOnTheEvaluator()
    {
        var database = new EmbeddedDatabase();
        database.RegisterCollation("BINARY", static (_, _) => 0);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value TEXT COLLATE BINARY);");
        Execute(connection, "INSERT INTO t VALUES ('first'), ('second');");

        var explain = ReadRows(connection, "EXPLAIN SELECT value FROM t WHERE value = 'missing';");
        Opcodes(explain).Should().Contain("Filter").And.NotContain("Compare");
        ReadRows(connection, "SELECT value FROM t WHERE value = 'missing';")
            .Should()
            .HaveCount(2);
    }

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

    private static List<SqlValue> DrainValues(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(statement.GetValue(0));

        return rows;
    }

    private static SqlValue[] ReadCurrentRow(EmbeddedStatement statement)
    {
        var values = new SqlValue[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
            values[ordinal] = statement.GetValue(ordinal);

        return values;
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
