using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies the Turso-compatible <c>get_byte</c>/<c>set_byte</c> scalar functions
/// (turso-sqltests/get-set-byte.sqltest) and the <c>json_object(*)</c> star expansion
/// (turso-sqltests/json_object_star.sqltest), both ported from the pinned
/// v0.8.0-pre.7 upstream.
/// </summary>
public class GetSetByteAndJsonObjectStarTests
{
    [Test]
    public void GetByteReadsBlobAndTextBytes()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT get_byte(x'1234567890', 4);").Should().Be("144");
        ReadScalar(connection, "SELECT get_byte(x'1234567890', 0);").Should().Be("18");
        ReadScalar(connection, "SELECT get_byte('ABC', 0);").Should().Be("65");
        ReadScalar(connection, "SELECT get_byte(x'1234567890', '4');").Should().Be("144");
    }

    [Test]
    public void GetByteNullAndRangeSemanticsMatchPostgres()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT get_byte(NULL, 4);").Should().BeEmpty();
        ReadScalar(connection, "SELECT get_byte(x'1234567890', NULL);").Should().BeEmpty();
        AssertThrows(connection, "SELECT get_byte(x'1234567890', 99);",
            "index 99 out of valid range, 0..4");
        AssertThrows(connection, "SELECT get_byte(x'1234567890', 5);",
            "index 5 out of valid range, 0..4");
        AssertThrows(connection, "SELECT get_byte(x'1234567890', -1);",
            "index -1 out of valid range, 0..4");
        AssertThrows(connection, "SELECT get_byte(x'', 0);",
            "index 0 out of valid range, 0..-1");
    }

    [Test]
    public void SetByteReplacesAndWrapsValues()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT hex(set_byte(x'1234567890', 4, 64));").Should().Be("1234567840");
        ReadScalar(connection, "SELECT hex(set_byte(x'1234567890', 0, 255));").Should().Be("FF34567890");
        ReadScalar(connection, "SELECT hex(set_byte(x'1234567890', 4, 6555));").Should().Be("123456789B");
        ReadScalar(connection, "SELECT hex(set_byte(x'1234567890', 4, -1));").Should().Be("12345678FF");
        ReadScalar(connection, "SELECT hex(set_byte('ABC', 0, 90));").Should().Be("5A4243");

        ReadScalar(connection, "SELECT hex(set_byte(NULL, 4, 64));").Should().BeEmpty();
        ReadScalar(connection, "SELECT hex(set_byte(x'1234567890', NULL, 64));").Should().BeEmpty();
        ReadScalar(connection, "SELECT hex(set_byte(x'1234567890', 4, NULL));").Should().BeEmpty();
        AssertThrows(connection, "SELECT set_byte(x'1234567890', 99, 11);",
            "index 99 out of valid range, 0..4");
        AssertThrows(connection, "SELECT set_byte(x'1234567890', -1, 11);",
            "index -1 out of valid range, 0..4");
    }

    [Test]
    public void JsonObjectStarExpandsTheCurrentRowColumns()
    {
        using var connection = Open();
        ExecuteScript(connection, """
            CREATE TABLE js_products (id INTEGER PRIMARY KEY, name TEXT, price REAL);
            INSERT INTO js_products VALUES (1, 'Widget', 9.99);
            INSERT INTO js_products VALUES (2, 'Gadget', 19.99);
            """);

        ReadScalar(connection, "SELECT json_object(*) FROM js_products WHERE id = 1;")
            .Should().Be("""{"id":1,"name":"Widget","price":9.99}""");
        ReadRows(connection, "SELECT json_object(*) FROM js_products ORDER BY id;")
            .Should().Equal(
                """{"id":1,"name":"Widget","price":9.99}""",
                """{"id":2,"name":"Gadget","price":19.99}""");
        ReadScalar(connection, "SELECT json_object(*) FROM js_products WHERE price > 10;")
            .Should().Be("""{"id":2,"name":"Gadget","price":19.99}""");
    }

    [Test]
    public void JsonObjectStarHandlesNullsAndRequiresFrom()
    {
        using var connection = Open();
        ExecuteScript(connection, """
            CREATE TABLE js_items (a INTEGER, b TEXT, c REAL);
            INSERT INTO js_items VALUES (1, NULL, 3.14);
            """);

        ReadScalar(connection, "SELECT json_object(*) FROM js_items;")
            .Should().Be("""{"a":1,"b":null,"c":3.14}""");

        AssertThrows(connection, "SELECT json_object(*);",
            "json_object(*) requires a FROM clause");
    }

    /// <summary>
    /// json_object(*) must expand correctly wherever it appears in an expression tree — not
    /// only when it is a projection's entire top-level expression — because the star can be
    /// nested inside another call or used as a WHERE predicate operand, and because a
    /// table-valued function's hidden columns must stay excluded even when the star is not
    /// evaluated through the runtime's raw-row fallback.
    /// </summary>
    [Test]
    public void JsonObjectStarExpandsWhenNestedOrUsedAsAPredicate()
    {
        using var connection = Open();
        ExecuteScript(connection, """
            CREATE TABLE js_products (id INTEGER PRIMARY KEY, name TEXT, price REAL);
            INSERT INTO js_products VALUES (1, 'Widget', 9.99);
            """);

        // Nested inside another function call: the star must still expand, not survive as a
        // CountStar call the runtime evaluator would otherwise read straight off the row.
        ReadScalar(connection, "SELECT upper(json_object(*)) FROM js_products WHERE id = 1;")
            .Should().Be("""{"ID":1,"NAME":"WIDGET","PRICE":9.99}""");

        // Used as a WHERE predicate operand over a table-valued function: generate_series's
        // hidden start/stop/step columns must stay excluded from the comparison, exactly as
        // they are excluded from a SELECT projection.
        ReadRows(
            connection,
            "SELECT value FROM generate_series(1,3) WHERE json_object(*) = json('{\"value\":1}');")
            .Should().Equal("1");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void JsonObjectStarExpandsInsideInlineAndNamedWindows(bool cancelable)
    {
        using var connection = Open();
        const string partition = """CASE WHEN json_object(*) = '{"value":1}' THEN 0 ELSE 1 END""";
        const string order = """CASE WHEN json_object(*) = '{"value":2}' THEN 0 ELSE value END""";

        ReadRows(connection, $"""
            SELECT value, count(*) OVER (PARTITION BY {partition}),
                row_number() OVER (ORDER BY {order})
            FROM generate_series(1,3) ORDER BY value;
            """, cancelable).Should().Equal("1|1|2", "2|2|1", "3|2|3");
        ReadRows(connection, $"""
            SELECT value, count(*) OVER w, row_number() OVER ordered
            FROM generate_series(1,3)
            WINDOW w AS (PARTITION BY {partition}), ordered AS (ORDER BY {order})
            ORDER BY value;
            """, cancelable).Should().Equal("1|1|2", "2|2|1", "3|2|3");
    }

    [TestCase("json_object(*)")]
    [TestCase("json(jsonb_object(*))")]
    public void JsonObjectStarExpandsInsideOrderedSetAggregates(string expression)
    {
        using var connection = Open();

        ReadScalar(connection, $"""
            SELECT percentile_disc(1) WITHIN GROUP (ORDER BY {expression})
            FROM generate_series(1,3);
            """).Should().Be("""{"value":3}""");
    }

    private static EmbeddedConnection Open()
    {
        var embedded = new EmbeddedDatabase();
        var connection = embedded.Connect();
        ExecuteScript(connection, "CREATE TABLE keep_open(x);");
        return connection;
    }

    private static void ExecuteScript(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static string ReadScalar(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).SingleOrDefault() ?? string.Empty;

    private static List<string> ReadRows(EmbeddedConnection connection, string sql, bool cancelable = false)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancelable ? cancellation.Token : default;
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(token) == StatementStepResult.Row)
                {
                    var values = new List<string>();
                    for (var column = 0; column < statement.ColumnCount; column++)
                    {
                        var value = statement.GetValue(column);
                        values.Add(value.Kind switch
                        {
                            SqlValueKind.Null => string.Empty,
                            SqlValueKind.Integer => value.AsInteger().ToString(),
                            SqlValueKind.Real => value.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                            SqlValueKind.Text => value.AsText(),
                            SqlValueKind.Blob => "x'" + Convert.ToHexString(value.AsBlob().Span) + "'",
                            _ => value.ToString() ?? string.Empty,
                        });
                    }

                    rows.Add(string.Join("|", values));
                }
            }
        }

        return rows;
    }

    private static void AssertThrows(EmbeddedConnection connection, string sql, string message)
    {
        var act = () => ReadRows(connection, sql);
        act.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain(message);
    }
}
