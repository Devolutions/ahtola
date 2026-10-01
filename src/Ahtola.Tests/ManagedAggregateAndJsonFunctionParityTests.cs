using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Locks in SQLite-compatible behavior for the aggregate and JSON builtins that
/// the managed engine previously rejected with "no such function".
/// </summary>
public sealed class ManagedAggregateAndJsonFunctionParityTests
{
    [Test]
    public void StringAggConcatenatesNonNullValuesWithTheRequiredSeparator()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT string_agg(v, '-') FROM t;").Should().Be(SqlValue.Text("x-y"));
        ReadValue(connection, "SELECT string_agg(k, '') FROM t;").Should().Be(SqlValue.Text("abc"));
    }

    [Test]
    public void StringAggReturnsNullForAnEmptyGroup()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT string_agg(v, '-') FROM t WHERE k = 'zzz';")
            .Should().Be(SqlValue.Null);
    }

    [Test]
    public void StringAggRequiresExactlyTwoArguments()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        var single = () => ReadValue(connection, "SELECT string_agg(v) FROM t;");
        single.Should().Throw<EmbeddedSqlException>()
            .WithMessage("wrong number of arguments to function string_agg()");

        var three = () => ReadValue(connection, "SELECT string_agg(v, '-', 3) FROM t;");
        three.Should().Throw<EmbeddedSqlException>()
            .WithMessage("wrong number of arguments to function string_agg()");
    }

    [Test]
    public void JsonGroupArrayKeepsNullRowsAsJsonNulls()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT json_group_array(v) FROM t;")
            .Should().Be(SqlValue.JsonText("[\"x\",\"y\",null]"));
    }

    [Test]
    public void JsonGroupArrayProducesAnEmptyArrayForAnEmptyGroup()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT json_group_array(n) FROM t WHERE k = 'zzz';")
            .Should().Be(SqlValue.JsonText("[]"));
    }

    [Test]
    public void JsonGroupArrayEmbedsNestedJsonInsteadOfQuotingIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT json_group_array(json_object('a', n)) FROM t;")
            .Should().Be(SqlValue.JsonText("[{\"a\":1},{\"a\":2},{\"a\":3}]"));
    }

    [Test]
    public void JsonGroupObjectBuildsAnObjectFromNameValuePairs()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT json_group_object(k, n) FROM t;")
            .Should().Be(SqlValue.JsonText("{\"a\":1,\"b\":2,\"c\":3}"));
        ReadValue(connection, "SELECT json_group_object(k, n) FROM t WHERE k = 'zzz';")
            .Should().Be(SqlValue.JsonText("{}"));
    }

    [Test]
    public void JsonGroupObjectSkipsRowsWithANullLabel()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        // SQLite's jsonObjectStep drops a row whose label is SQL NULL, and Turso matches it
        // since 517ec809f ("json: skip NULL labels in object aggregates"). A NULL *value* is
        // still kept as a JSON null.
        ReadValue(connection, "SELECT json_group_object(v, n) FROM t;")
            .Should().Be(SqlValue.JsonText("{\"x\":1,\"y\":2}"));
        ReadValue(connection, "SELECT json(jsonb_group_object(v, n)) FROM t;")
            .Should().Be(SqlValue.JsonText("{\"x\":1,\"y\":2}"));
        ReadValue(connection, "SELECT json_group_object(k, v) FROM t;")
            .Should().Be(SqlValue.JsonText("{\"a\":\"x\",\"b\":\"y\",\"c\":null}"));
        ReadValue(connection, "SELECT json_group_object(NULL, n) FROM t;")
            .Should().Be(SqlValue.JsonText("{}"));
        ReadValue(connection, "SELECT json(jsonb_group_object(NULL, n)) FROM t;")
            .Should().Be(SqlValue.JsonText("{}"));
        ReadColumn(connection, "SELECT json_group_object(v, n) OVER (ORDER BY n) FROM t;")
            .Should().Equal("{\"x\":1}", "{\"x\":1,\"y\":2}", "{\"x\":1,\"y\":2}");
    }

    [Test]
    public void JsonAggregatesReportSqliteArityDiagnostics()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        var array = () => ReadValue(connection, "SELECT json_group_array(v, n) FROM t;");
        array.Should().Throw<EmbeddedSqlException>()
            .WithMessage("wrong number of arguments to function json_group_array()");

        var obj = () => ReadValue(connection, "SELECT json_group_object(k) FROM t;");
        obj.Should().Throw<EmbeddedSqlException>()
            .WithMessage("wrong number of arguments to function json_group_object()");
    }

    [Test]
    public void JsonAggregatesGroupAndWindowLikeOtherBuiltInAggregates()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadColumn(connection, "SELECT json_group_array(n) FROM t GROUP BY k ORDER BY k;")
            .Should().Equal("[1]", "[2]", "[3]");
        ReadColumn(connection, "SELECT json_group_array(n) OVER () FROM t;")
            .Should().Equal("[1,2,3]", "[1,2,3]", "[1,2,3]");
        ReadColumn(connection, "SELECT string_agg(k, '-') OVER () FROM t;")
            .Should().Equal("a-b-c", "a-b-c", "a-b-c");
    }

    [Test]
    public void JsonbFunctionsUseSQLiteBinaryEncodingAndRoundTripThroughJson()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Seed(connection);

        ReadValue(connection, "SELECT hex(jsonb_group_object(1, 2));")
            .Should().Be(SqlValue.Text("4C17311332"));
        ReadValue(connection, "SELECT hex(jsonb_group_object(1.5, 2));")
            .Should().Be(SqlValue.Text("6C37312E351332"));
        ReadValue(connection, "SELECT jsonb(NULL);")
            .Should().Be(SqlValue.Null);
        ReadValue(connection, "SELECT hex(jsonb('null'));")
            .Should().Be(SqlValue.Text("00"));
        ReadValue(connection, "SELECT json(jsonb_group_object(1, jsonb_array(2)));")
            .Should().Be(SqlValue.JsonText("{\"1\":[2]}"));
        ReadValue(connection, "SELECT json(jsonb_set(jsonb_object('a', 1), '$.b', 2));")
            .Should().Be(SqlValue.JsonText("{\"a\":1,\"b\":2}"));
        ReadValue(connection, "SELECT json(jsonb_extract(jsonb_array(1, 2), '$'));")
            .Should().Be(SqlValue.JsonText("[1,2]"));
    }

    [Test]
    public void JsonbMutatorsPreserveJsonMutatorArityDiagnostics()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var insert = () => ReadValue(connection, "SELECT jsonb_insert('{}', '$.a');");
        insert.Should().Throw<EmbeddedSqlException>()
            .WithMessage("json_insert() needs an odd number of arguments");

        var replace = () => ReadValue(connection, "SELECT jsonb_replace('{}', '$.a');");
        replace.Should().Throw<EmbeddedSqlException>()
            .WithMessage("json_replace() needs an odd number of arguments");
    }

    [TestCase("SELECT json_pretty('{\"a\":1,\"b\":[1,2],\"c\":{}}');",
        "{\n    \"a\": 1,\n    \"b\": [\n        1,\n        2\n    ],\n    \"c\": {}\n}")]
    [TestCase("SELECT json_pretty('[1,{\"x\":null}]');",
        "[\n    1,\n    {\n        \"x\": null\n    }\n]")]
    [TestCase("SELECT json_pretty('[]');", "[]")]
    [TestCase("SELECT json_pretty('{}');", "{}")]
    [TestCase("SELECT json_pretty('1');", "1")]
    [TestCase("SELECT json_pretty('\"s\"');", "\"s\"")]
    [TestCase("SELECT json_pretty('{\"a\":1}', '  ');", "{\n  \"a\": 1\n}")]
    [TestCase("SELECT json_pretty('{\"a\":1}', '');", "{\n\"a\": 1\n}")]
    public void JsonPrettyRendersSqliteIndentation(string sql, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // json_pretty returns plain text rather than a JSON-subtyped value.
        ReadValue(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [Test]
    public void JsonPrettyPropagatesNullAndRejectsMalformedInput()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        ReadValue(connection, "SELECT json_pretty(NULL);").Should().Be(SqlValue.Null);

        var malformed = () => ReadValue(connection, "SELECT json_pretty('{bad');");
        malformed.Should().Throw<EmbeddedSqlException>().WithMessage("malformed JSON");

        var arity = () => ReadValue(connection, "SELECT json_pretty();");
        arity.Should().Throw<EmbeddedSqlException>()
            .WithMessage("wrong number of arguments to function json_pretty()");
    }

    private static void Seed(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE t(k TEXT, v TEXT, n INTEGER);");
        Execute(connection, "INSERT INTO t VALUES('a','x',1),('b','y',2),('c',NULL,3);");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static List<string> ReadColumn(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsText());

        return values;
    }
}
