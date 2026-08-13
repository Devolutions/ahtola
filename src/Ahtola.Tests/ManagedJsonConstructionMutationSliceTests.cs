using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class ManagedJsonConstructionMutationSliceTests
{
    [Test]
    public void ConstructorsAndQuotePreserveJsonAndBoundTextSemantics()
    {
        AssertText(
            "json_array(1, 1.5, 'a\"b', NULL)",
            "[1,1.5,\"a\\\"b\",null]");
        AssertText(
            "json_object('x', 1, 'nested', json_array(2, 3))",
            "{\"x\":1,\"nested\":[2,3]}");
        AssertText(
            "json_quote(json_object('a', 1))",
            "{\"a\":1}");
        AssertText(
            "json_quote('a\"b')",
            "\"a\\\"b\"");
        AssertText(
            "json_array(?1)",
            "[\"{\\\"a\\\":1}\"]",
            SqlValue.Text("{\"a\":1}"));
    }

    [Test]
    public void MutatorsHandleNestedPathsSequentialUpdatesAndJsonValues()
    {
        AssertText(
            "json_set('{}', '$.items[0].name', 'Ada', '$.items[0].active', 1)",
            "{\"items\":[{\"name\":\"Ada\",\"active\":1}]}");
        AssertText(
            "json_insert('{\"a\":1,\"items\":[10]}', '$.a', 2, '$.items[#]', 20)",
            "{\"a\":1,\"items\":[10,20]}");
        AssertText(
            "json_replace('{\"profile\":{\"old\":1},\"other\":0}', '$.profile', json_object('name', 'Ada'), '$.other', NULL)",
            "{\"profile\":{\"name\":\"Ada\"},\"other\":null}");
        AssertText(
            "json_remove('{\"a\":[1,2,3],\"b\":{\"x\":1,\"y\":2}}', '$.a[0]', '$.a[#-1]', '$.b.x')",
            "{\"a\":[2],\"b\":{\"y\":2}}");
        AssertText(
            "json_patch('{\"user\":{\"name\":\"Ada\",\"age\":20},\"keep\":true}', '{\"user\":{\"age\":21},\"keep\":null,\"roles\":[\"admin\"]}')",
            "{\"user\":{\"name\":\"Ada\",\"age\":21},\"roles\":[\"admin\"]}");
        AssertInteger("json_array_length('{\"items\":[1,2,3]}', '$.items')", 3);
        AssertNull("json_array_length('{\"items\":[1,2,3]}', '$.missing')");
    }

    [Test]
    public void ErrorPositionAndInvalidInputsHaveExplicitFailures()
    {
        AssertInteger("json_error_position('{]')", 2);
        AssertInteger("json_error_position('{\"a\":}')", 6);
        AssertInteger("json_error_position('{\"a\":[1,true,null]}')", 0);
        AssertNull("json_error_position(NULL)");

        AssertText("json_set('{\"x\":1}', '$.x[', 1)", "{\"x\":1}");
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_object('only-key')"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_array(x'ff')"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_set('{}', '$.x[', 1)"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_insert('{}', '$.value')"))
            .Message.Should().Be("json_insert() needs an odd number of arguments");
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_replace('{}', '$.value')"))
            .Message.Should().Be("json_replace() needs an odd number of arguments");
        // JSON5 unquoted object keys are accepted by every value-producing entry point
        // (Turso jsonb.rs deserialize_obj); json_valid() stays RFC-strict.
        AssertText("json('{unquoted:1}')", "{\"unquoted\":1}");
        AssertInteger("json_valid('{unquoted:1}')", 0);
        AssertText("json(jsonb_array(1))", "[1]");
    }

    [Test]
    public void CoreJsonSubsetPreservesNullPathAndReturnTypeSemantics()
    {
        AssertInteger("json_array_length('[1,2,3]')", 3);
        AssertInteger("json_array_length('{\"a\":1}')", 0);
        AssertNull("json_array_length('{\"a\":1}', '$.missing')");
        AssertNull("json_array_length(NULL)");

        AssertText("json_quote(NULL)", "null");
        AssertText(
            "json_quote(json_extract('{\"value\":{\"nested\":1}}', '$.value'))",
            "{\"nested\":1}");
        AssertText(
            "json_quote(json_extract('{\"value\":\"{}\"}', '$.value'))",
            "\"{}\"");
        AssertText("json_set('{}', NULL, 1)", "{}");
        AssertNull("json_set(NULL, '$.a', 1)");
        AssertText(
            "json_set('{}', '$.value', json_extract('{\"nested\":[1,2]}', '$.nested'))",
            "{\"value\":[1,2]}");
        AssertText("json_remove('{\"a\":1,\"b\":2}', '$.a')", "{\"b\":2}");
        AssertNull("json_remove('{\"a\":1}', '$')");
        AssertNull("json_remove(NULL, '$.a')");

        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_array_length('not json')"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("json_valid('[1,]', 2)"));
    }

    [Test]
    public void ArrowOperatorsMatchSqliteExtractionAndSubtypeSemantics()
    {
        const string document = """'{"array":[10,20,30],"text":"x","boolean":true,"nothing":null,"object":{"key":1}}'""";

        AssertText($"{document} -> '$.array'", "[10,20,30]");
        AssertText($"{document} ->> '$.array'", "[10,20,30]");
        AssertText($"{document} -> '$.text'", "\"x\"");
        AssertText($"{document} -> '$.boolean'", "true");
        AssertText($"{document} -> '$.nothing'", "null");
        AssertText($"{document} -> '$.object'", """{"key":1}""");

        AssertText($"{document} ->> '$.text'", "x");
        AssertInteger($"{document} ->> '$.boolean'", 1);
        AssertNull($"{document} ->> '$.nothing'");
        AssertText($"{document} ->> '$.object'", """{"key":1}""");

        AssertText("'[10,20,30]' -> 1", "20");
        AssertInteger("'[10,20,30]' ->> 1", 20);
        AssertText("'[10,20,30]' -> -1", "30");
        AssertInteger("'[10,20,30]' ->> -1", 30);
        AssertText("""'{"a.b":1,"a":{"b":2}}' -> 'a.b'""", "1");
        AssertInteger("""'{"a.b":1,"a":{"b":2}}' ->> '$.a.b'""", 2);
        AssertInteger("""'{"a":{"b":7}}' -> 'a' ->> 'b'""", 7);
        AssertText("""'{"text":"x"}' -> '$.text' || '!'""", "\"x\"!");
        AssertNull($"{document} -> '$.missing'");
        AssertNull($"{document} ->> '$.missing'");
        AssertNull($"{document} -> 1.0");
        AssertNull($"NULL -> '$.array'");
        Assert.Throws<EmbeddedSqlException>(() => Scalar("'[1]' -> '$[bad]'"));
        Assert.Throws<EmbeddedSqlException>(() => Scalar("""'{"":2}' -> ''"""));

        AssertText("""json_array('{"array":[1,2]}' -> '$.array')""", "[[1,2]]");
        AssertText("""json_array('{"array":[1,2]}' ->> '$.array')""", """["[1,2]"]""");
    }

    [Test]
    public void JsonSubtypeDoesNotCrossBindingOrStorageBoundaries()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var extract = connection.Prepare("SELECT json_extract('{\"value\":[1,2]}', '$.value');");
        extract.Step().Should().Be(StatementStepResult.Row);

        using var boundQuote = connection.Prepare("SELECT json_quote(?1);");
        boundQuote.Bind(1, extract.GetValue(0));
        boundQuote.Step().Should().Be(StatementStepResult.Row);
        boundQuote.GetValue(0).Should().Be(SqlValue.Text("\"[1,2]\""));

        foreach (var statement in connection.PrepareScript("""
            CREATE TABLE json_subtype_boundary(value TEXT);
            INSERT INTO json_subtype_boundary
            VALUES(json_extract('{"value":[1,2]}', '$.value'));
            """))
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }

        using var select = connection.Prepare("SELECT json_quote(value) FROM json_subtype_boundary;");
        select.Step().Should().Be(StatementStepResult.Row);
        select.GetValue(0).Should().Be(SqlValue.Text("\"[1,2]\""));
    }

    [Test]
    public void JsonSubtypeDoesNotCrossQueryMaterializationBoundaries()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        AssertText(
            connection,
            "SELECT json_array(value) FROM (SELECT json('[1,2]') AS value);",
            "[\"[1,2]\"]");
        AssertText(
            connection,
            "WITH boundary(value) AS (SELECT json('[1,2]')) SELECT json_array(value) FROM boundary;",
            "[\"[1,2]\"]");

        Execute(connection, "CREATE VIEW json_boundary AS SELECT json('[1,2]') AS value;");
        AssertText(connection, "SELECT json_array(value) FROM json_boundary;", "[\"[1,2]\"]");
        AssertText(
            connection,
            "SELECT json_array(value) FROM (SELECT json('[1,2]') AS value UNION ALL SELECT json('[3,4]'));",
            "[\"[1,2]\"]",
            "[\"[3,4]\"]");
        AssertText(
            connection,
            """
            WITH RECURSIVE boundary(value, depth) AS (
                SELECT json('[1,2]'), 1
                UNION ALL
                SELECT value, depth + 1 FROM boundary WHERE depth < 2
            )
            SELECT json_array(value) FROM boundary ORDER BY depth;
            """,
            "[\"[1,2]\"]",
            "[\"[1,2]\"]");
    }

    private static SqlValue Scalar(string expression, params SqlValue[] parameters)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT " + expression + ";");
        for (int i = 0; i < parameters.Length; i++)
            statement.Bind(i + 1, parameters[i]);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void AssertText(string expression, string expected, params SqlValue[] parameters)
        => Scalar(expression, parameters).Should().Be(SqlValue.Text(expected), because: expression);

    private static void AssertText(EmbeddedConnection connection, string sql, params string[] expected)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<SqlValue>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0));

        values.Should().Equal(expected.Select(SqlValue.Text), because: sql);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static void AssertInteger(string expression, long expected)
        => Scalar(expression).Should().Be(SqlValue.Integer(expected), because: expression);

    private static void AssertNull(string expression)
        => Scalar(expression).Should().Be(SqlValue.Null, because: expression);
}
