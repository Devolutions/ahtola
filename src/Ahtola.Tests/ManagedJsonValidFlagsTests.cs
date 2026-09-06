using System.Globalization;
using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Feature coverage for the flagged json_valid(X, Y) surface, the JSONB strict
/// validator, subtype(), and the JSON5 leniencies the vendored json-valid-strict
/// corpus pins, exercised on the embedded engine directly.
/// </summary>
public class ManagedJsonValidFlagsTests
{
    [Test]
    public void TwoArgJsonValidFlags()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid('{a:1}', 1), json_valid('{a:1}', 2), json_valid('{a:1}', 3), json_valid('{a:1}', 15)").Should().Be("0|1|1|1");
        Read(connection, "SELECT json_valid('{\"a\":1}', 1), json_valid('{\"a\":1}', 2)").Should().Be("1|1");
    }

    [Test]
    public void NumberMantissaRules()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid('.', 2), json_valid('+e1', 2), json_valid('-e1', 2), json_valid('.5', 2), json_valid('5.', 2), json_valid('+.5', 2)").Should().Be("0|0|0|1|1|1");
        Read(connection, "SELECT json_error_position('.'), json_error_position('.e1'), json_error_position('+.'), json_error_position('-.'), json_error_position('+'), json_error_position('[.5, .]')").Should().Be("1|1|3|3|1|6");
        Read(connection, "SELECT json_valid('0..', 2), json_valid('1.2.3', 2), json_valid('1e1e1', 2), json_valid('1e1.5', 2), json_valid('5.e2', 2)").Should().Be("0|0|0|0|1");
        Read(connection, "SELECT json_error_position('0..'), json_error_position('1.2.3'), json_error_position('1e1e1'), json_error_position('1e1.5')").Should().Be("3|4|4|4");
    }

    [Test]
    public void BlobFlagClassification()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid(jsonb('{\"a\":1}'), 2), json_valid(jsonb('{\"a\":1}'), 4), json_valid(jsonb('{\"a\":1}'), 8), json_valid(x'ABCD', 4), json_valid(x'ABCD', 8)").Should().Be("0|1|1|0|0");
        Read(connection, "SELECT json_valid(x'3B133132', 4), json_valid(x'3B133132', 8)").Should().Be("1|0");
        Read(connection, "SELECT json_valid(x'1301', 8), json_valid(x'13FF', 8), json_valid(x'1331', 8)").Should().Be("0|0|1");
        Read(connection, "SELECT json_valid(x'C000', 8), json_valid(x'C100', 8), json_valid(x'C200', 8), json_valid(x'00', 8)").Should().Be("0|0|0|1");
        Read(connection, "SELECT json_valid(x'2033343535', 1), json_valid(x'2033343535', 2), json_valid(x'2033343535', 4), json_valid(x'2033343535', 8)").Should().Be("1|1|0|0");
        Read(connection, "SELECT json_valid(x'5B6E756C6C5D', 1), json_valid(x'5B6E756C6C5D', 2), json_valid(x'5B6E756C6C5D', 4), json_valid(x'5B6E756C6C5D', 8)").Should().Be("1|1|0|0");
        Read(connection, "SELECT json_valid('[1]', 4), json_valid('[1]', 8), json_valid('[1,2,3]', 6)").Should().Be("0|0|1");
    }

    [Test]
    public void FlagCoercionAndErrors()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid(123, 1), json_valid(123, 2), json_valid(123, 4)").Should().Be("1|1|0");
        Read(connection, "SELECT json_valid(9e999, 1), json_valid(9e999, 2)").Should().Be("0|1");
        Read(connection, "SELECT json_valid(9e999), json_valid(-9e999)").Should().Be("0|0");
        Read(connection, "SELECT json_valid(NULL, 1) IS NULL").Should().Be("1");
        Read(connection, "SELECT json_valid('{}', '2'), json_valid('{}', 2.5)").Should().Be("1|1");
        Read(connection, "SELECT json_valid(x'2B1331', '1e1'), json_valid('{}', '1e1')").Should().Be("0|1");
        AssertFlagsError(connection, "SELECT json_valid('{}', 0)");
        AssertFlagsError(connection, "SELECT json_valid('{}', 16)");
        AssertFlagsError(connection, "SELECT json_valid(NULL, NULL)");
    }

    [Test]
    public void TextEscapeFlag8Checks()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid(x'38615C71', 8), json_valid(x'38615C71', 4), json_valid(x'38615C6E', 8), json_valid(x'78615C7530303431', 8), json_valid(x'78615C755A5A5A5A', 8), json_valid(x'39615C71', 8), json_valid(x'49615C3035', 8), json_valid(x'59615C785A5A', 8), json_valid(x'2A615C', 8)").Should().Be("0|0|1|1|0|0|0|1|1");
        Read(connection, "SELECT json_valid(x'17FF', 8), json_valid(x'18FF', 8), json_valid(x'19FF', 8), json_valid(x'1AFF', 8), json_valid(x'C9055C0AC35C71', 8)").Should().Be("1|1|1|1|0");
        Read(connection, "SELECT json_valid(x'C8025C00', 8), json_valid(x'C9025C00', 8), json_valid(x'C7025C00', 8)").Should().Be("1|1|0");
        Read(connection, "SELECT json_valid(x'7B295C3033313233', 8)").Should().Be("1");
        Read(connection, "SELECT json_valid(x'C9065C755A5A5A5A', 8), json_valid(x'C9085C0A5C755A5A5A5A', 8)").Should().Be("0|1");
        Read(connection, "SELECT json_valid(x'FB0000000000000000', 4), json_valid(x'FB0000000000000000', 8), json_valid(x'9BFB0000000000000000', 8), json_valid(x'F3000000000000000133', 8), json_valid(x'FB0000000100000000', 4), json_valid(x'FB0000000100000000', 8)").Should().Be("1|1|1|1|0|0");
        Read(connection, "SELECT json_valid(x'CB09F00000000000000000', 8), json_valid(x'CB09F00000000000000000', 4)").Should().Be("0|1");
    }

    [Test]
    public void ErrorPositionJsonbOffsets()
    {
        using var connection = Open();
        Read(connection, "SELECT json_error_position(x'3B133132'), json_error_position(x'1301'), json_error_position(x'2B31FF'), json_error_position(x'5B6E756C6C5D'), json_error_position(x'22FF22'), json_error_position(x'CB01')").Should().Be("4|2|2|0|0|1");
        Read(connection, "SELECT json_error_position(x'313233'), json_error_position(x'3132330078'), json_error_position(x'ABCD')").Should().Be("0|0|1");
    }

    [Test]
    public void SubtypeReportsJsonText()
    {
        using var connection = Open();
        Read(connection, "SELECT subtype(json('{}')), subtype(jsonb('{}')), subtype('{}'), subtype(1), subtype(NULL), subtype(json_extract('{\"a\":[1]}','$.a')), subtype(json_extract('{\"a\":1}','$.a'))").Should().Be("74|0|0|0|0|74|0");
        Read(connection, "SELECT subtype('{\"a\":[7,8]}'->'a'), subtype('{\"a\":[7,8]}'->>'a'), typeof('{\"a\":[7,8]}'->>'a')").Should().Be("74|0|text");
        Read(connection, "SELECT subtype(json_quote('x')), subtype(json_quote(5)), subtype(json_quote(NULL)), subtype(json_patch('{}','null')), subtype(json_patch('{\"a\":1}','{\"b\":2}')), subtype(json_type('{}')), subtype(json_type('[3]','$[0]')), subtype(json_pretty('{}'))").Should().Be("74|74|74|74|74|0|0|0");
        Read(connection, "SELECT json_array(json_type('{}'))").Should().Be("[\"object\"]");
        Read(connection, "SELECT json_patch('{}', jsonb('null')), subtype(json_patch('{}', jsonb('null'))), json_patch('{}', x'00'), json_patch('{}', 'null'), json_patch('{\"a\":1}', 'null'), hex(jsonb_patch('{}', jsonb('null'))), hex(jsonb_patch('{}', 'null'))").Should().Be("null|74|null|null|null|00|00");
    }

    [Test]
    public void BadPathMessages()
    {
        using var connection = Open();
        AssertBadPath(connection, "SELECT json_extract('{}', 'x''y''z')", "bad JSON path: 'x''y''z'");
        AssertBadPath(connection, "SELECT json_extract('{}', 'x' || char(0) || 'y')", "bad JSON path: 'x'");
    }

    [Test]
    public void Json5StringAndKeyLeniencies()
    {
        using var connection = Open();
        Read(connection, "SELECT ('{a: \"abc' || char(0x5c, 0x0a) || 'xyz\"}') ->> 'a'").Should().Be("abcxyz");
        Read(connection, "SELECT ('{a: \"abc' || char(0x5c, 0x0d) || 'xyz\"}') ->> 'a'").Should().Be("abcxyz");
        Read(connection, "SELECT ('{a: \"abc' || char(0x5c, 0x0d, 0x0a) || 'xyz\"}') ->> 'a'").Should().Be("abcxyz");
        Read(connection, "SELECT ('{a: \"abc' || char(0x5c, 0x2028) || 'xyz\"}') ->> 'a', ('{a: \"abc' || char(0x5c, 0x2029) || 'xyz\"}') ->> 'a'").Should().Be("abcxyz|abcxyz");
        Read(connection, "SELECT json('{a: \"abc' || char(0x5c, 0x0d) || 'xyz\"}')").Should().Be("{\"a\":\"abcxyz\"}");
        Read(connection, "SELECT hex(('{a: \"x' || char(0x5c, 0x76) || 'z\"}') ->> 'a'), json('{a: \"x' || char(0x5c, 0x76) || 'z\"}')").Should().Be("780B7A|{\"a\":\"x\\u0009z\"}");
        Read(connection, "SELECT ('{a: \"abc' || char(0x5c, 0x22) || 'xyz\"}') ->> 'a'").Should().Be("abc\"xyz");
    }

    [Test]
    public void Json5UnquotedKeys()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid('{x1:5}',2), json_valid('{_x:5}',2), json_valid('{$x:5}',2), json_valid('{éx:5}',2)").Should().Be("1|1|1|1");
        Read(connection, "SELECT json_valid('{1x:5}',2), json_valid('{x-y:5}',2), json_valid('{x.y:5}',2), json_valid('{x/y:5}',2), json_valid('{:5}',2), json_valid('{a'||char(1)||':1}',2)").Should().Be("0|0|0|0|0|0");
        Read(connection, "SELECT json('{' || char(0x5c) || 'u0061:1}'), ('{' || char(0x5c) || 'u0061:1}')->>'a', ('{a' || char(0x5c) || 'u0062:1}')->>'ab', json('{' || char(0x5c) || 'u0031:1}')").Should().Be("{\"\\u0061\":1}|1|1|{\"\\u0031\":1}");
        Read(connection, "SELECT json_valid('{a' || char(0x5c) || 'tb:1}', 2), json_valid('{a' || char(0x5c) || 'x62:1}', 2), json_valid('{' || char(0x5c) || 'u061:1}', 2)").Should().Be("0|0|0");
        Read(connection, "SELECT json('{x/*c*/:1}'), json('{x//c' || char(10) || ':1}'), json_valid('{x/y:1}', 2)").Should().Be("{\"x\":1}|{\"x\":1}|0");
    }

    [Test]
    public void TextStopsAtFirstNul()
    {
        using var connection = Open();
        Read(connection, "SELECT json('123' || char(0) || 'x'), json_extract('{\"a\":7}' || char(0) || 'junk', '$.a'), json_array_length('[1,2]' || char(0) || 'zzz'), json_error_position('\"a' || char(0) || '\"x'), json_insert('{}', '$.a', 'x' || char(0) || 'y')").Should().Be("123|7|2|3|{\"a\":\"x\\u0000y\"}");
    }

    [Test]
    public void Json5MarkingSurvivesStandardEscapes()
    {
        using var connection = Open();
        Read(connection, "SELECT json_valid('\"\\x41\\n\"'), json_valid('\"a' || char(1) || '\\n\"'), json_valid('\"\\n\\x41\"'), hex(jsonb('\"\\x41\\n\"'))").Should().Be("0|0|0|695C7834315C6E");
    }

    [Test]
    public void InfinityAndTrailingDot()
    {
        using var connection = Open();
        Read(connection, "SELECT '{x:Infinity}'->'x', typeof('{x:Infinity}'->'x')").Should().Be("9e999|text");
        Read(connection, "SELECT '{x:Infinity}'->>'x', typeof('{x:Infinity}'->>'x'), '{x:-Infinity}'->>'x', json_extract('{x:Infinity}','$.x')").Should().Be("Inf|real|-Inf|Inf");
        Read(connection, "SELECT json('Infinity'), json('{x:-Infinity}'), json('{\"x\":9e999}'), json_quote(9e999)").Should().Be("9e999|{\"x\":-9e999}|{\"x\":9e999}|9.0e+999");
        Read(connection, "SELECT '+9.0e+999'->'$', '{x: +9.0e+999}'->'$.x', typeof('+9.0e+999'->>'$')").Should().Be("9.0e+999|9.0e+999|real");
        Read(connection, "SELECT json('{x: 4.}'), json('{x: 4.e0}'), json('{x: +4.e1}'), json('{x: -4.e2}')").Should().Be("{\"x\":4.0}|{\"x\":4.0e0}|{\"x\":4.0e1}|{\"x\":-4.0e2}");
        Read(connection, "SELECT CAST(('{x: 4.e0}')->>'x' AS TEXT), CAST(('{x: +4.e1}')->>'x' AS TEXT), CAST(('{x: -4.e2}')->>'x' AS TEXT)").Should().Be("4.0|40.0|-400.0");
        Read(connection, "SELECT json('{x: +4e1}'), json('{x: +5.5}'), json('{x: 4.E2}')").Should().Be("{\"x\":4e1}|{\"x\":5.5}|{\"x\":4.0E2}");
    }

    [Test]
    public void BlobStructuralArguments()
    {
        using var connection = Open();
        Read(connection, "SELECT json_array(jsonb_array(1,2)), json(jsonb_array(json_array(1,2))), json_object('ex', jsonb('[52,3.14159]')), json_set('{\"a\":2,\"c\":4}', '$.c', jsonb('[97,96]')), json_array(x'0B'), json_quote(x'0B')").Should().Be("[[1,2]]|[[1,2]]|{\"ex\":[52,3.14159]}|{\"a\":2,\"c\":[97,96]}|[[]]|[]");
        var act = () => Read(connection, "SELECT json_array(x'313233')");
        act.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain("JSON cannot hold BLOB values");
        Read(connection, "SELECT json_patch(jsonb('{\"a\":1}'), jsonb('{\"b\":2}')), json(jsonb_patch(jsonb('{\"a\":1}'), jsonb('{\"b\":2}'))), json_patch(x'313233', '{}'), json_error_position(jsonb('{\"a\":1}')), json_error_position(x'313233'), json_error_position(x'ABCD')").Should().Be("{\"a\":1,\"b\":2}|{\"a\":1,\"b\":2}|{}|0|0|1");
    }

    [Test]
    public void PatchValidatesTargetBeforeNullPatch()
    {
        using var connection = Open();
        var act = () => Read(connection, "SELECT json_patch(x'ABCD', 'null')");
        act.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain("malformed JSON");
        var act2 = () => Read(connection, "SELECT jsonb_patch(x'ABCD', 'null')");
        act2.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain("malformed JSON");
    }

    private static void AssertFlagsError(EmbeddedConnection connection, string sql)
    {
        var act = () => Read(connection, sql);
        act.Should().Throw<EmbeddedSqlException>()
            .Which.Message.Should().Contain("FLAGS parameter to json_valid() must be between 1 and 15");
    }

    private static void AssertBadPath(EmbeddedConnection connection, string sql, string message)
    {
        var act = () => Read(connection, sql);
        act.Should().Throw<EmbeddedSqlException>().Which.Message.Should().Contain(message);
    }

    private static EmbeddedConnection Open()
    {
        var embedded = new EmbeddedDatabase();
        return embedded.Connect();
    }

    private static string Read(EmbeddedConnection connection, string sql)
    {
        var rows = new List<string>();
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(default) == StatementStepResult.Row)
                {
                    rows.Add(string.Join("|", Enumerable.Range(0, statement.ColumnCount)
                        .Select(index => Format(statement.GetValue(index)))));
                }
            }
        }

        return string.Join("\n", rows);
    }

    private static string Format(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => string.Empty,
            SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span),
            SqlValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            SqlValueKind.Real => double.IsInfinity(value.AsReal())
                ? (double.IsPositive(value.AsReal()) ? "Inf" : "-Inf")
                : value.AsReal().ToString("0.############################", CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            _ => throw new InvalidOperationException(),
        };
}
