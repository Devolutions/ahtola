using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ArithmeticSqlRoutingTests
{
    [Test]
    public void GenericSourceLessProjectionsLowerNestedArithmeticAndFunctions()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare(
            "SELECT ?1 + (2 * ?2), abs(?3 + 1), upper(?4), ?1;");
        statement.Bind(1, SqlValue.Integer(3));
        statement.Bind(2, SqlValue.Integer(4));
        statement.Bind(3, SqlValue.Integer(-6));
        statement.Bind(4, SqlValue.Text("mixed"));

        statement.Step().Should().Be(StatementStepResult.Row);
        Row(statement).Should().Equal(
            SqlValue.Integer(11),
            SqlValue.Integer(5),
            SqlValue.Text("MIXED"),
            SqlValue.Integer(3));

        var explain = Explain(
            connection,
            "EXPLAIN SELECT ?1 + (2 * ?2), abs(?3 + 1), upper(?4), ?1;",
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null);
        Opcodes(explain).Count(opcode => opcode == "Arithmetic").Should().Be(3);
        Opcodes(explain).Count(opcode => opcode == "Function").Should().Be(2);
        Opcodes(explain).Count(opcode => opcode == "LoadParameter").Should().Be(5);
        Opcodes(explain).Count(opcode => opcode == "NumericAffinity").Should().Be(6);
    }

    [Test]
    public void NumericAffinityMatchesEvaluatorForEveryStorageClass()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Value(connection, "SELECT ? + 1", SqlValue.Text("10")).Should().Be(SqlValue.Integer(11));
        Value(connection, "SELECT ? + 1", SqlValue.Text("not-a-number")).Should().Be(SqlValue.Integer(1));

        // SQLite numerifies a blob from its bytes, so x'3130' reads as "10".
        Value(connection, "SELECT ? + 1", SqlValue.Blob([0x31, 0x30])).Should().Be(SqlValue.Integer(11));
        Value(connection, "SELECT ? + 1", SqlValue.Blob([0x61, 0x62])).Should().Be(SqlValue.Integer(1));
        Value(connection, "SELECT ? + 1", SqlValue.Null).Should().Be(SqlValue.Null);
        Value(connection, "SELECT ? / 0", SqlValue.Integer(5)).Should().Be(SqlValue.Null);
        Value(connection, "SELECT ? % 3", SqlValue.Text("10xyz")).Should().Be(SqlValue.Integer(1));

        Opcodes(Explain(connection, "EXPLAIN SELECT ? + 1", SqlValue.Text("10")))
            .Should().ContainInOrder("LoadParameter", "LoadConstant", "NumericAffinity", "NumericAffinity", "Arithmetic");
    }

    [Test]
    public void ResetAndRebindReuseValueIndependentParameterShape()
    {
        using var connection = new EmbeddedDatabase().Connect();
        using var statement = connection.Prepare("SELECT ?1 + 1, typeof(?1);");

        statement.Bind(1, SqlValue.Integer(2));
        statement.Step().Should().Be(StatementStepResult.Row);
        Row(statement).Should().Equal(SqlValue.Integer(3), SqlValue.Text("integer"));

        statement.Reset();
        statement.Bind(1, SqlValue.Text("10"));
        statement.Step().Should().Be(StatementStepResult.Row);
        Row(statement).Should().Equal(SqlValue.Integer(11), SqlValue.Text("text"));

        var integerPlan = Dump(Explain(connection, "EXPLAIN SELECT ?1 + 1, typeof(?1);", SqlValue.Integer(2)));
        var textPlan = Dump(Explain(connection, "EXPLAIN SELECT ?1 + 1, typeof(?1);", SqlValue.Text("10")));
        integerPlan.Should().Equal(textPlan);
        integerPlan.Should().Contain(entry => entry.Contains("param[0]", StringComparison.Ordinal));
    }

    [Test]
    public void NullShortCircuitsBeforeAffinityAndFunctionErrorsPropagate()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Value(connection, "SELECT NULL + ?", SqlValue.Blob([0xFF])).Should().Be(SqlValue.Null);

        using var statement = connection.Prepare("SELECT 1, abs(?);");
        statement.Bind(1, SqlValue.Integer(long.MinValue));
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Be("integer overflow");
    }

    [Test]
    public void SafeConstantArithmeticFoldsWhileFunctionsExecuteAtRuntime()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Read(connection, "SELECT 1 + 2, abs(-5);")[0]
            .Should().Equal(SqlValue.Integer(3), SqlValue.Integer(5));
        Opcodes(Read(connection, "EXPLAIN SELECT 1 + 2, abs(-5);"))
            .Should().Equal("LoadConstant", "LoadConstant", "Function", "ResultRow", "Halt");
    }

    [Test]
    public void VolatileNestedExpressionsRemainEvaluatorOwnedAndRunPerRow()
    {
        var calls = 0L;
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "next_value",
            0,
            _ => SqlValue.Integer(++calls));
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE input(value);");
        Execute(connection, "INSERT INTO input VALUES (1), (2), (3);");

        var rows = Read(connection, "SELECT next_value() + 0 FROM input;");

        rows.Select(row => row[0]).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Integer(3));
        calls.Should().Be(3);
        ExplainRefused(connection, "EXPLAIN SELECT next_value() + 0 FROM input;");

        var uuids = Read(connection, "SELECT upper(uuid4_str()) FROM input;")
            .Select(row => row[0].AsText())
            .ToArray();
        uuids.Distinct(StringComparer.Ordinal).Should().HaveCount(3);
        ExplainRefused(connection, "EXPLAIN SELECT upper(uuid4_str()) FROM input;");
    }

    [Test]
    public void ErroringRowIndependentFunctionIsNotEvaluatedForEmptyInput()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE empty_input(value);");

        var compiled = Read(
            connection,
            "SELECT abs(-9223372036854775808) FROM empty_input;");
        var evaluated = Read(
            connection,
            "SELECT CASE WHEN 1 THEN abs(-9223372036854775808) END FROM empty_input;");

        AssertRowsEqual(compiled, evaluated);
        Opcodes(Read(
                connection,
                "EXPLAIN SELECT abs(-9223372036854775808) FROM empty_input;"))
            .Should().ContainInOrder("Rewind", "LoadConstant", "Function");
    }

    [Test]
    public void ConcatenationAndComparisonUseCompiledProjectionLowering()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Value(connection, "SELECT ? || 'x'", SqlValue.Text("a")).Should().Be(SqlValue.Text("ax"));
        Value(connection, "SELECT ? < 5", SqlValue.Integer(3)).Should().Be(SqlValue.Integer(1));
        Opcodes(Explain(connection, "EXPLAIN SELECT ? || 'x'", SqlValue.Text("a")))
            .Should().Contain("Function");
        Opcodes(Explain(connection, "EXPLAIN SELECT ? < 5", SqlValue.Integer(3)))
            .Should().Contain("Compare");
    }

    [Test]
    public void CompiledExpressionsMatchEvaluatorFallback()
    {
        using var connection = new EmbeddedDatabase().Connect();
        var parameterCases = new[]
        {
            new[] { SqlValue.Integer(4), SqlValue.Real(2.5), SqlValue.Null, SqlValue.Text("MIXED") },
            new[] { SqlValue.Text("10"), SqlValue.Text("3x"), SqlValue.Text("set"), SqlValue.Text("unused") },
            new[] { SqlValue.Null, SqlValue.Blob([0x31]), SqlValue.Null, SqlValue.Text("LOWER") },
        };

        foreach (var parameters in parameterCases)
        {
            var compiled = Read(
                connection,
                "SELECT ?1 + (?2 * 2), typeof(?1), coalesce(?3, lower(?4));",
                parameters);
            var evaluated = Read(
                connection,
                """
                SELECT CASE WHEN 1 THEN ?1 + (?2 * 2) END,
                       CASE WHEN 1 THEN typeof(?1) END,
                       CASE WHEN 1 THEN coalesce(?3, lower(?4)) END;
                """,
                parameters);

            AssertRowsEqual(compiled, evaluated);
        }

        Execute(connection, "CREATE TABLE expr(a, b);");
        Execute(connection, "INSERT INTO expr VALUES (10, 2), ('7x', 3), (NULL, 4), (x'31', 5);");
        var compiledScan = Read(
            connection,
            "SELECT a + (?1 * b), abs(b - a), upper(typeof(a)) FROM expr;",
            SqlValue.Integer(2));
        var evaluatedScan = Read(
            connection,
            """
            SELECT CASE WHEN 1 THEN a + (?1 * b) END,
                   CASE WHEN 1 THEN abs(b - a) END,
                   CASE WHEN 1 THEN upper(typeof(a)) END
            FROM expr;
            """,
            SqlValue.Integer(2));

        AssertRowsEqual(compiledScan, evaluatedScan);
    }

    private static SqlValue Value(EmbeddedConnection connection, string sql, params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        Bind(statement, parameters);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static SqlValue[] Row(EmbeddedStatement statement)
        => Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetValue).ToArray();

    private static List<SqlValue[]> Explain(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        Bind(statement, parameters);
        return Drain(statement);
    }

    private static void ExplainRefused(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        Bind(statement, parameters);
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())!
            .Message.Should().Contain("EXPLAIN is only supported");
    }

    private static List<SqlValue[]> Read(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        Bind(statement, parameters);
        return Drain(statement);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static void AssertRowsEqual(IReadOnlyList<SqlValue[]> actual, IReadOnlyList<SqlValue[]> expected)
    {
        actual.Should().HaveCount(expected.Count);
        for (var index = 0; index < actual.Count; index++)
            actual[index].Should().Equal(expected[index]);
    }

    private static List<SqlValue[]> Drain(EmbeddedStatement statement)
    {
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(Row(statement));
        return rows;
    }

    private static void Bind(EmbeddedStatement statement, IReadOnlyList<SqlValue> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
            statement.Bind(index + 1, parameters[index]);
    }

    private static IEnumerable<string> Opcodes(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => row[1].AsText());

    private static List<string> Dump(IEnumerable<SqlValue[]> rows)
        => rows.Select(row => $"{row[1].AsText()}|{row[6].AsText()}").ToList();
}
