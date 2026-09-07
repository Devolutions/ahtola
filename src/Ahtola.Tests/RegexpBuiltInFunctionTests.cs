using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies the built-in sqlean-compatible regexp family: the <c>X REGEXP Y</c> operator
/// and <c>regexp(pattern, source)</c> work without user registration (the pinned
/// <c>turso-src/core/regexp.rs</c> registers the scalar), with the documented coercion
/// semantics (<c>to_text_coerced</c>: integers/reals as text, blobs as UTF-8 bytes, NULL
/// as NULL, invalid patterns as NULL), plus the <c>regexp_like</c> / <c>regexp_substr</c> /
/// <c>regexp_replace</c> / <c>regexp_capture</c> extension functions
/// (<c>turso-src/extensions/regexp</c>). The corpus source is
/// <c>conformance/sqlite-sqltests/turso/regexp.sqltest</c>.
/// </summary>
public class RegexpBuiltInFunctionTests
{
    [Test]
    public void RegexpOperatorMatchesWithoutRegistration()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT 'hello' REGEXP 'h.*o';").Should().Be("1");
        ReadScalar(connection, "SELECT 'hello' REGEXP '^world$';").Should().Be("0");
        ReadScalar(connection, "SELECT 'hello' NOT REGEXP 'h.*o';").Should().Be("0");
        ReadScalar(connection, "SELECT 'hello' NOT REGEXP '^world$';").Should().Be("1");
    }

    [Test]
    public void RegexpFunctionCallMatchesTheOperator()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT regexp('h.*o', 'hello');").Should().Be("1");
        ReadScalar(connection, "SELECT regexp('^world$', 'hello');").Should().Be("0");
    }

    [Test]
    public void RegexpCoercesNumericAndBlobOperandsToText()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT 123 REGEXP '1.*3';").Should().Be("1");
        ReadScalar(connection, "SELECT 456 REGEXP '1.*3';").Should().Be("0");
        ReadScalar(connection, "SELECT 3.14 REGEXP '3\\.14';").Should().Be("1");
        ReadScalar(connection, "SELECT 3.14 REGEXP '3\\.99';").Should().Be("0");
        ReadScalar(connection, "SELECT 45.6 REGEXP '45';").Should().Be("1");
        ReadScalar(connection, "SELECT -1.5 REGEXP '-1';").Should().Be("1");
        ReadScalar(connection, "SELECT -42 REGEXP '-4';").Should().Be("1");
        ReadScalar(connection, "SELECT -42 REGEXP '99';").Should().Be("0");
        ReadScalar(connection, "SELECT 0 REGEXP '^0$';").Should().Be("1");
        ReadScalar(connection, "SELECT X'414243' REGEXP 'ABC';").Should().Be("1");
        ReadScalar(connection, "SELECT X'414243' REGEXP '41';").Should().Be("0");
        ReadScalar(connection, "SELECT X'68656C6C6F' REGEXP 'hello';").Should().Be("1");
        ReadScalar(connection, "SELECT 'hello' REGEXP 123;").Should().Be("0");
        ReadScalar(connection, "SELECT 123 REGEXP 456;").Should().Be("0");
    }

    [Test]
    public void RegexpNullOperandsAndInvalidPatternsYieldNull()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT NULL REGEXP 'abc';").Should().BeEmpty();
        ReadScalar(connection, "SELECT 'abc' REGEXP NULL;").Should().BeEmpty();
        ReadScalar(connection, "SELECT regexp(NULL, 'abc');").Should().BeEmpty();
        ReadScalar(connection, "SELECT regexp('abc', NULL);").Should().BeEmpty();

        // Wrong arity is the only error; upstream's varargs shim used to panic on it.
        var wrongArity = () => ReadScalar(connection, "SELECT regexp('a');");
        wrongArity.Should().Throw<EmbeddedSqlException>()
            .Which.Message.Should().Contain("wrong number of arguments to function regexp()");
    }

    [Test]
    public void RegexpWorksInWhereSelectCaseAndSubquery()
    {
        using var connection = Open();
        ExecuteScript(connection, """
            CREATE TABLE t1(a TEXT, b INTEGER);
            INSERT INTO t1 VALUES ('abc', 1), ('def', 2), ('abcdef', 3), ('xyz', 4);
            """);

        ReadRows(connection, "SELECT b FROM t1 WHERE a REGEXP '^abc';").Should().Equal("1", "3");
        ReadRows(connection, "SELECT b FROM t1 WHERE a NOT REGEXP '^abc';").Should().Equal("2", "4");
        ReadRows(connection, "SELECT a, a REGEXP 'def$' FROM t1;")
            .Should().Equal("abc|0", "def|1", "abcdef|1", "xyz|0");
        ReadRows(connection, "SELECT CASE WHEN a REGEXP '^a' THEN 'starts_a' ELSE 'other' END FROM t1;")
            .Should().Equal("starts_a", "other", "starts_a", "other");
        ReadRows(connection, """
            SELECT * FROM (SELECT a, a REGEXP 'def' AS matches FROM t1) sub WHERE matches = 1 ORDER BY a DESC
            """).Should().Equal("def|1", "abcdef|1");
    }

    [Test]
    public void RegexpHandlesComplexPatterns()
    {
        using var connection = Open();

        ReadScalar(connection, "SELECT 'test@example.com' REGEXP '^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\\.[a-zA-Z]{2,}$';")
            .Should().Be("1");
        ReadScalar(connection, "SELECT '2024-01-15' REGEXP '^\\d{4}-\\d{2}-\\d{2}$';").Should().Be("1");
        ReadScalar(connection, "SELECT 'hello world' REGEXP '\\bworld\\b';").Should().Be("1");
    }

    [Test]
    public void RegexpLikeSubstrReplaceAndCaptureMatchTheExtensionSemantics()
    {
        using var connection = Open();

        // regexp_like(source, pattern) swaps the operands of regexp.
        ReadScalar(connection, "SELECT regexp_like('hello', 'h.*o');").Should().Be("1");
        ReadScalar(connection, "SELECT regexp_like('hello', '^world$');").Should().Be("0");

        // regexp_substr returns the first matching substring.
        ReadScalar(connection, "SELECT regexp_substr('hello world', 'w[a-z]+');").Should().Be("world");
        ReadScalar(connection, "SELECT regexp_substr('hello', '^x');").Should().BeEmpty();

        // regexp_replace replaces the first match with $-group expansion.
        ReadScalar(connection, "SELECT regexp_replace('hello world', 'world', 'there');")
            .Should().Be("hello there");
        ReadScalar(connection, "SELECT regexp_replace('a1b2', '[0-9]', '');").Should().Be("ab2");
        ReadScalar(connection, "SELECT regexp_replace('a1b2', '([a-z])([0-9])', '$2$1');").Should().Be("1ab2");

        // regexp_capture returns the first capture group (default 1).
        ReadScalar(connection, "SELECT regexp_capture('key=value', 'key=(.*)');").Should().Be("value");
        ReadScalar(connection, "SELECT regexp_capture('2024-01-15', '(\\d+)-(\\d+)-(\\d+)', 3);").Should().Be("15");
        ReadScalar(connection, "SELECT regexp_capture('abc', 'x(y)');").Should().BeEmpty();
    }

    [Test]
    public void RegexpIsValidPatternReturnsNullNotError()
    {
        using var connection = Open();

        // The corpus pins that an invalid pattern yields NULL (the Rust regex compile
        // failure path), so '(' as a pattern must not raise.
        var rows = new List<string>();
        var act = () =>
        {
            rows.Clear();
            foreach (var statement in connection.PrepareScript("SELECT regexp('(', 'abc'); SELECT 'abc' REGEXP '(';"))
            {
                using (statement)
                {
                    while (statement.Step(default) == StatementStepResult.Row)
                        rows.Add(statement.GetValue(0).Kind == SqlValueKind.Null ? "<null>" : "x");
                }
            }
        };
        act.Should().NotThrow();
        rows.Should().Equal("<null>", "<null>");
    }

    private static EmbeddedConnection Open()
    {
        var embedded = new EmbeddedDatabase();
        var connection = embedded.Connect();
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
}
