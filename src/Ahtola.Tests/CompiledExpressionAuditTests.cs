using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class CompiledExpressionAuditTests
{
    [Test]
    public void TruthTestsUseCompiledSqlTruthSemantics()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT ?1 IS TRUE, ?1 IS FALSE, ?1 IS NOT TRUE, ?1 IS NOT FALSE;");
        statement.Bind(1, SqlValue.Text("2"));

        ReadRow(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(0),
            SqlValue.Integer(0),
            SqlValue.Integer(1));

        statement.Reset();
        statement.Bind(1, SqlValue.Null);
        ReadRow(statement).Should().Equal(
            SqlValue.Integer(0),
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Integer(1));

        ExplainFunctions(connection, "EXPLAIN SELECT ?1 IS TRUE;", SqlValue.Integer(2))
            .Should().Contain("is_true");
        ExplainOpcodes(connection, "EXPLAIN SELECT ?1 IS TRUE;", SqlValue.Integer(2))
            .Should().Contain("Function").And.NotContain("Compare");
    }

    [Test]
    public void BetweenAndNotBetweenLowerToComparisonsAndThreeValuedAnd()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT ?1 BETWEEN ?2 AND ?3, ?1 NOT BETWEEN ?2 AND ?3;");
        statement.Bind(1, SqlValue.Integer(2));
        statement.Bind(2, SqlValue.Integer(1));
        statement.Bind(3, SqlValue.Integer(3));

        ReadRow(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(0));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(4));
        statement.Bind(2, SqlValue.Null);
        statement.Bind(3, SqlValue.Integer(3));
        ReadRow(statement).Should().Equal(SqlValue.Integer(0), SqlValue.Integer(1));

        var opcodes = ExplainOpcodes(
            connection,
            "EXPLAIN SELECT ?1 BETWEEN ?2 AND ?3, ?1 NOT BETWEEN ?2 AND ?3;",
            SqlValue.Integer(2),
            SqlValue.Integer(1),
            SqlValue.Integer(3));
        opcodes.Count(opcode => opcode == "Compare").Should().Be(4);
        var functions = ExplainFunctions(
            connection,
            "EXPLAIN SELECT ?1 BETWEEN ?2 AND ?3, ?1 NOT BETWEEN ?2 AND ?3;",
            SqlValue.Integer(2),
            SqlValue.Integer(1),
            SqlValue.Integer(3));
        functions.Count(function => function == "and").Should().Be(2);
        functions.Should().Contain("not");
    }

    [Test]
    public void InListProjectionPreservesNullsNegationAndEarlyMatch()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT ?1 IN (?2, ?3, NULL), ?1 NOT IN (?2, ?3, NULL);");
        statement.Bind(1, SqlValue.Integer(2));
        statement.Bind(2, SqlValue.Integer(1));
        statement.Bind(3, SqlValue.Integer(2));

        ReadRow(statement).Should().Equal(SqlValue.Integer(1), SqlValue.Integer(0));

        statement.Reset();
        statement.Bind(1, SqlValue.Integer(4));
        statement.Bind(2, SqlValue.Integer(1));
        statement.Bind(3, SqlValue.Integer(2));
        ReadRow(statement).Should().Equal(SqlValue.Null, SqlValue.Null);

        using var lazy = connection.Prepare("SELECT ?1 IN (?2, abs(-9223372036854775808));");
        lazy.Bind(1, SqlValue.Integer(7));
        lazy.Bind(2, SqlValue.Integer(7));
        ReadRow(lazy).Should().Equal(SqlValue.Integer(1));

        var opcodes = ExplainOpcodes(
            connection,
            "EXPLAIN SELECT ?1 IN (?2, ?3);",
            SqlValue.Integer(2),
            SqlValue.Integer(1),
            SqlValue.Integer(2));
        opcodes.Should().Contain("Compare").And.Contain("Function").And.Contain("JumpIf");
        opcodes.Should().NotContain("OpenEphemeral");
        ExplainFunctions(
                connection,
                "EXPLAIN SELECT ?1 IN (?2, ?3);",
                SqlValue.Integer(2),
                SqlValue.Integer(1),
                SqlValue.Integer(2))
            .Should().Contain("or");
    }

    [Test]
    public void LogicalOperatorsAndUnaryNotLowerWithLazyBranches()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT ?1 AND ?2, ?1 OR ?2, NOT ?1;");
        statement.Bind(1, SqlValue.Null);
        statement.Bind(2, SqlValue.Integer(0));

        ReadRow(statement).Should().Equal(
            SqlValue.Integer(0),
            SqlValue.Null,
            SqlValue.Null);

        using var lazy = connection.Prepare("SELECT ?1 OR abs(-9223372036854775808);");
        lazy.Bind(1, SqlValue.Integer(1));
        ReadRow(lazy).Should().Equal(SqlValue.Integer(1));

        var opcodes = ExplainOpcodes(
            connection,
            "EXPLAIN SELECT ?1 AND ?2, ?1 OR ?2, NOT ?1;",
            SqlValue.Integer(1),
            SqlValue.Integer(0));
        opcodes.Should().Contain("Function").And.Contain("JumpIf");
        ExplainFunctions(
                connection,
                "EXPLAIN SELECT ?1 AND ?2, ?1 OR ?2, NOT ?1;",
                SqlValue.Integer(1),
                SqlValue.Integer(0))
            .Should().Contain(["and", "or", "not", "is_false", "is_true"]);
    }

    [Test]
    public void ConcatenationLowersWithoutEvaluatorFallback()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT ?1 || ?2;");
        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Real(3.5));

        ReadRow(statement).Should().Equal(SqlValue.Text("73.5"));

        statement.Reset();
        statement.Bind(1, SqlValue.Null);
        statement.Bind(2, SqlValue.Text("x"));
        ReadRow(statement).Should().Equal(SqlValue.Null);

        ExplainFunctions(
                connection,
                "EXPLAIN SELECT ?1 || ?2;",
                SqlValue.Text("a"),
                SqlValue.Text("b"))
            .Should().Contain("concat");
    }

    [Test]
    public void LikeAndGlobLowerThroughEvaluatorCompatibleFunctions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare(
            "SELECT ?1 LIKE ?2, ?1 NOT LIKE ?2, ?1 GLOB ?3, ?1 NOT GLOB ?4, ?5 LIKE ?6 ESCAPE ?7;");
        statement.Bind(1, SqlValue.Text("Abc"));
        statement.Bind(2, SqlValue.Text("a%"));
        statement.Bind(3, SqlValue.Text("A*"));
        statement.Bind(4, SqlValue.Text("a*"));
        statement.Bind(5, SqlValue.Text("a_c"));
        statement.Bind(6, SqlValue.Text("a!_c"));
        statement.Bind(7, SqlValue.Text("!"));

        ReadRow(statement).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Integer(1));

        statement.Reset();
        statement.Bind(1, SqlValue.Null);
        statement.Bind(5, SqlValue.Null);
        ReadRow(statement).Should().Equal(
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null);

        ExplainFunctions(
                connection,
                "EXPLAIN SELECT ?1 LIKE ?2, ?1 NOT LIKE ?2, ?1 GLOB ?3, ?1 NOT GLOB ?4, ?5 LIKE ?6 ESCAPE ?7;",
                SqlValue.Text("Abc"),
                SqlValue.Text("a%"),
                SqlValue.Text("A*"),
                SqlValue.Text("a*"),
                SqlValue.Text("a_c"),
                SqlValue.Text("a!_c"),
                SqlValue.Text("!"))
            .Should().Contain(["like", "not_like", "glob", "not_glob"]);

        Execute(connection, "CREATE TABLE patterns(value TEXT);");
        Execute(connection, "INSERT INTO patterns VALUES ('Abc');");
        ReadRows(connection, "SELECT value LIKE 'a%', value GLOB 'A*' FROM patterns;")
            .Single().Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        ExplainOpcodes(connection, "EXPLAIN SELECT value GLOB 'A*' FROM patterns;")
            .Should().Contain("Column").And.Contain("Function");
    }

    [Test]
    public void SimpleCaseAcceptsArbitraryCompiledBaseAndWhenExpressions()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value INTEGER);");
        Execute(connection, "INSERT INTO t VALUES (1), (2);");

        ReadRows(connection, "SELECT CASE value + 1 WHEN value * 2 THEN 'match' ELSE 'miss' END FROM t;")
            .Select(row => row[0])
            .Should().Equal(SqlValue.Text("match"), SqlValue.Text("miss"));
        ReadRows(connection, "SELECT CASE NULL WHEN NULL THEN 'match' ELSE 'miss' END;")
            .Single().Should().Equal(SqlValue.Text("miss"));

        var opcodes = ExplainOpcodes(
            connection,
            "EXPLAIN SELECT CASE value + 1 WHEN value * 2 THEN 'match' ELSE 'miss' END FROM t;");
        opcodes.Should().Contain("Arithmetic").And.Contain("Compare").And.Contain("JumpIfNotTrue");
    }

    private static SqlValue[] ReadRow(EmbeddedStatement statement)
    {
        statement.Step().Should().Be(StatementStepResult.Row);
        var row = Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetValue).ToArray();
        statement.Step().Should().Be(StatementStepResult.Done);
        return row;
    }

    private static List<string> ExplainOpcodes(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

        var opcodes = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            opcodes.Add(statement.GetValue(1).AsText());
        return opcodes;
    }

    private static List<string> ExplainFunctions(
        EmbeddedConnection connection,
        string sql,
        params SqlValue[] parameters)
    {
        using var statement = connection.Prepare(sql);
        for (var index = 0; index < parameters.Length; index++)
            statement.Bind(index + 1, parameters[index]);

        var functions = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            if (statement.GetValue(1).AsText() == "Function")
                functions.Add(statement.GetValue(5).AsText());
        }

        return functions;
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(Enumerable.Range(0, statement.GetColumnCount()).Select(statement.GetValue).ToArray());
        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }
}
