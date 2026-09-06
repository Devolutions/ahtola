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

    private static List<string> ReadRows(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
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
