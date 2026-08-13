using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// Differential coverage for the managed date/time and JSON scalar functions.
//
// Every expected value in the *_MatchSqlite tests below was captured from CPython's bundled
// SQLite 3.50.4 engine (see gen_asserts.py in the repo root during development), so these
// assertions are a direct differential check against upstream SQLite behavior. Time-dependent
// functions ("now", localtime, utc) are verified structurally instead of against a fixed value.
public class DateTimeJsonFunctionTests
{
    [Test]
    public void DateTimeFunctions_MatchSqlite()
    {
        AssertText("date('2023-06-15')", "2023-06-15");
        AssertText("date('2023-06-15 14:30:45')", "2023-06-15");
        AssertText("date('2023-06-15T14:30:45')", "2023-06-15");
        AssertText("date('2023-06-15T14:30:45.123')", "2023-06-15");
        AssertText("date('2023-06-15 14:30:45.999+02:00')", "2023-06-15");
        AssertText("date('2023-06-15 14:30:45-05:00')", "2023-06-15");
        AssertText("date('2023-06-15 01:30:00+05:00')", "2023-06-14");
        AssertText("date('2023-06-15 22:30:00Z')", "2023-06-15");
        AssertText("date('2023-01-31','+1 month')", "2023-03-03");
        AssertText("date('2023-02-30')", "2023-03-02");
        AssertText("date('2023-03-31','-1 month')", "2023-03-03");
        AssertText("date('2023-12-31','+1 day')", "2024-01-01");
        AssertText("date('2024-02-29','+1 year')", "2025-03-01");
        AssertText("date('2020-02-29','+4 years')", "2024-02-29");
        AssertText("date('2023-06-15 14:30:45','start of month')", "2023-06-01");
        AssertText("date('2023-06-15 14:30:45','start of year')", "2023-01-01");
        AssertText("datetime('2023-06-15 14:30:45','start of day')", "2023-06-15 00:00:00");
        AssertText("datetime('2023-06-15','start of month','+1 month','-1 day')", "2023-06-30 00:00:00");
        AssertText("date('2023-06-15','weekday 0')", "2023-06-18");
        AssertText("date('2023-06-15','weekday 6')", "2023-06-17");
        AssertText("date('2023-06-15','weekday 1')", "2023-06-19");
        AssertText("datetime('2023-06-15 14:30:45','+1 day')", "2023-06-16 14:30:45");
        AssertText("datetime('2023-06-15 14:30:45','-2 hours')", "2023-06-15 12:30:45");
        AssertText("datetime('2023-06-15 14:30:45','+90 minutes')", "2023-06-15 16:00:45");
        AssertText("datetime('2023-06-15 14:30:45','+30 seconds')", "2023-06-15 14:31:15");
        AssertText("datetime('2023-06-15 14:30:45','+1 day','+2 hours','-15 minutes')", "2023-06-16 16:15:45");
        AssertText("datetime('2023-06-15','+1.5 days')", "2023-06-16 12:00:00");
        AssertText("datetime('2023-06-15 00:00:00','+0.5 hours')", "2023-06-15 00:30:00");
        AssertText("date('2023-06-15','+1 year','+2 months','+10 days')", "2024-08-25");
        AssertText("datetime('2023-06-15 14:30:00','1 day')", "2023-06-16 14:30:00");
        AssertText("datetime('2023-06-15 14:30:00','0000-00-01 12:00:00')", "2023-06-17 02:30:00");
        AssertText("datetime('2023-06-15 14:30:00','+0000-00-01 12:00:00')", "2023-06-17 02:30:00");
        AssertText("datetime('2023-06-15','+0001-00-00')", "2024-06-15 00:00:00");
        AssertText("datetime('2023-06-15','-0000-00-01')", "2023-06-14 00:00:00");
        AssertText("datetime('2023-06-15','0000-00-01')", "2023-06-16 00:00:00");
        AssertText("time('2023-06-15 14:30:45')", "14:30:45");
        AssertText("time('14:30:45')", "14:30:45");
        AssertText("time('14:30')", "14:30:00");
        AssertText("time('2023-06-15 14:30:45.123','subsec')", "14:30:45.123");
        AssertText("datetime('2023-06-15 14:30:45.123','subsec')", "2023-06-15 14:30:45.123");
        AssertText("time('2023-06-15 14:30:45.5','subsecond')", "14:30:45.500");
        AssertReal("julianday('1970-01-01 00:00:00')", 2440587.5);
        AssertReal("julianday('2000-01-01 12:00:00')", 2451545.0);
        AssertReal("julianday('2023-06-15 12:00:00')", 2460111.0);
        AssertInt("unixepoch('1970-01-01 00:00:00')", 0);
        AssertInt("unixepoch('1969-12-31 23:59:59')", -1);
        AssertInt("unixepoch('2023-06-15 14:30:45')", 1686839445);
        AssertReal("unixepoch('2023-06-15 14:30:45.5','subsec')", 1686839445.5);
        AssertText("date(2451545.0)", "2000-01-01");
        AssertText("datetime(2451545.0)", "2000-01-01 12:00:00");
        AssertText("datetime(0)", "-4713-11-24 12:00:00");
        AssertText("date(2451545.0,'julianday')", "2000-01-01");
        AssertText("datetime(1686839445,'unixepoch')", "2023-06-15 14:30:45");
        AssertText("datetime(1686839445,'unixepoch','+1 day')", "2023-06-16 14:30:45");
        AssertText("datetime('1686839445','unixepoch')", "2023-06-15 14:30:45");
        AssertText("date('2023-06-15','auto')", "2023-06-15");
        AssertText("datetime(1686839445.5,'unixepoch','subsec')", "2023-06-15 14:30:45.500");
        AssertNull("date('not a date')");
        AssertNull("date('2023-13-01')");
        AssertNull("date('2023-06-32')");
        AssertNull("date('10000-01-01')");
        AssertNull("datetime('2023-06-15','+1 fortnight')");
        AssertNull("datetime('2023-06-15','garbage')");
        AssertNull("date(NULL)");
        AssertNull("date('2023-06-15',NULL)");
        AssertNull("date(x'00')");
        AssertNull("time('2023-06-15 25:00:00')");
    }

    [Test]
    public void Strftime_MatchesSqlite()
    {
        AssertText("strftime('%Y-%m-%d','2023-06-15 14:30:45')", "2023-06-15");
        AssertText("strftime('%H:%M:%S','2023-06-15 14:30:45')", "14:30:45");
        AssertText("strftime('%Y-%m-%dT%H:%M:%S','2023-06-15 14:30:45')", "2023-06-15T14:30:45");
        AssertText("strftime('%j','2023-06-15')", "166");
        AssertText("strftime('%J','2000-01-01 12:00:00')", "2451545");
        AssertText("strftime('%s','1970-01-01 00:00:01')", "1");
        AssertText("strftime('%w','2023-06-15')", "4");
        AssertText("strftime('%W','2023-06-15')", "24");
        AssertText("strftime('%U','2023-06-15')", "24");
        AssertText("strftime('%G-W%V-%u','2023-01-01')", "2022-W52-7");
        AssertText("strftime('%G-W%V-%u','2023-01-02')", "2023-W01-1");
        AssertText("strftime('%g','2023-01-01')", "22");
        AssertText("strftime('%p %I:%M','2023-06-15 14:30:00')", "PM 02:30");
        AssertText("strftime('%p %I:%M','2023-06-15 00:30:00')", "AM 12:30");
        AssertText("strftime('%P','2023-06-15 09:00:00')", "am");
        AssertText("strftime('%e','2023-06-05')", " 5");
        AssertText("strftime('%F %T','2023-06-15 14:30:45')", "2023-06-15 14:30:45");
        AssertText("strftime('%R','2023-06-15 14:30:45')", "14:30");
        AssertText("strftime('%f','2023-06-15 14:30:45.678')", "45.678");
        AssertText("strftime('%%','2023-06-15')", "%");
        AssertText("strftime('literal %Y text','2023-06-15')", "literal 2023 text");
        AssertNull("strftime('%Q','2023-06-15')");
        AssertText("strftime('%Y-','2023-06-15')", "2023-");
        AssertText("strftime('%k|%l','2023-06-15 05:00:00')", " 5| 5");
        AssertText("strftime('%u','2023-06-18')", "7");
        AssertNull("strftime(NULL,'2023-06-15')");
        AssertNull("strftime('%Y',NULL)");
        AssertNull("strftime('%Y','not a date')");
        AssertText("strftime('%s',1686839445,'unixepoch')", "1686839445");
    }

    [Test]
    public void JsonFunctions_MatchSqlite()
    {
        AssertText("json('  [1.0, 2e3, 3.140]  ')", "[1.0,2e3,3.140]");
        AssertText("json('{ \"x\" : 1 , \"y\":[true,false,null] }')", "{\"x\":1,\"y\":[true,false,null]}");
        AssertText("json('\"a\\u0041b\"')", "\"a\\u0041b\"");
        AssertText("json(x'7b7d')", "{}");
        AssertText("json(5)", "5");
        AssertText("json(5.0)", "5.0");
        AssertText("json(0.0)", "0.0");
        AssertText("json(1.5)", "1.5");
        AssertText("json(-2.5)", "-2.5");
        AssertText("json(100.0)", "100.0");
        AssertText("json(10000000000.0)", "10000000000.0");
        AssertText("json('123')", "123");
        AssertText("json('1.5e10')", "1.5e10");
        AssertText("json('[]')", "[]");
        AssertText("json('  true ')", "true");
        AssertText("json('null')", "null");
        AssertText("json('{\"a\":1,\"a\":2}')", "{\"a\":1,\"a\":2}");
        AssertThrows("json('01')");
        AssertThrows("json('nul')");
        AssertThrows("json('\"abc')");
        AssertNull("json(NULL)");
        AssertNull("json_valid(NULL)");
        AssertInt("json_valid(5)", 1);
        AssertInt("json_valid(5.5)", 1);
        AssertInt("json_valid('[1,2,3]')", 1);
        AssertInt("json_valid('01')", 0);
        AssertInt("json_valid('1e999')", 1);
        AssertInt("json_valid('NaN')", 0);
        AssertInt("json_valid('  { } ')", 1);
        AssertInt("json_valid('nul')", 0);
        AssertInt("json_valid(x'7b7d')", 1);
        AssertInt("json_valid('1.')", 0);
        AssertInt("json_valid('.5')", 0);
        AssertInt("json_valid('[1, 2, ]')", 0);
        AssertInt("json_valid('{\"a\":1,}')", 0);
        AssertInt("json_valid('')", 0);
        AssertInt("json_valid('\"\\u0041\"')", 1);
        AssertInt("json_valid('[1 2]')", 0);
        AssertInt("json_valid('123abc')", 0);
        AssertNull("json_type(NULL)");
        AssertText("json_type('null')", "null");
        AssertText("json_type('true')", "true");
        AssertText("json_type('false')", "false");
        AssertText("json_type('123')", "integer");
        AssertText("json_type('1.5')", "real");
        AssertText("json_type('1e3')", "real");
        AssertText("json_type('99999999999999999999999')", "integer");
        AssertText("json_type('\"hi\"')", "text");
        AssertText("json_type('[1,2]')", "array");
        AssertText("json_type('{}')", "object");
        AssertText("json_type(123)", "integer");
        AssertText("json_type(1.5)", "real");
        AssertText("json_type('{\"a\":1}', '$.a')", "integer");
        AssertText("json_type('{\"a\":[1,2]}', '$.a')", "array");
        AssertNull("json_type('{\"a\":1}', '$.b')");
        AssertNull("json_type('{\"a\":1}', NULL)");
        AssertThrows("json_type('bad')");
        AssertText("json_extract('{\"a\":\"b\\nc\"}','$.a')", "b\nc");
        AssertInt("json_extract('{\"a\":true}','$.a')", 1);
        AssertInt("json_extract('{\"a\":false}','$.a')", 0);
        AssertNull("json_extract('{\"a\":null}','$.a')");
        AssertInt("json_extract('{\"a\":123}','$.a')", 123);
        AssertReal("json_extract('{\"a\":1.5}','$.a')", 1.5);
        AssertReal("json_extract('{\"a\":1e3}','$.a')", 1000.0);
        AssertReal("json_extract('[1e999]','$[0]')", double.PositiveInfinity);
        AssertText("json_extract('{\"a\":[1, 2,  3]}','$.a')", "[1,2,3]");
        AssertText("json_extract('{\"a\":{\"b\":1}}','$.a')", "{\"b\":1}");
        AssertInt("json_extract('[10,20,30]','$[0]')", 10);
        AssertInt("json_extract('[10,20,30]','$[2]')", 30);
        AssertInt("json_extract('[10,20,30]','$[#-1]')", 30);
        AssertNull("json_extract('[10,20,30]','$[#]')");
        AssertNull("json_extract('[10,20,30]','$[3]')");
        AssertInt("json_extract('[10,20,30]','$[#-3]')", 10);
        AssertNull("json_extract('[10,20,30]','$[#-9]')");
        AssertInt("json_extract('[10,20,30]','$[01]')", 20);
        AssertNull("json_extract('{\"a\":1}','$.b')");
        AssertNull("json_extract('{\"a\":1}')");
        AssertText("json_extract('{\"a\":1}', '$.a', '$.b')", "[1,null]");
        AssertText("json_extract('{\"c\":true,\"e\":null}','$.c','$.e')", "[true,null]");
        AssertNull("json_extract(NULL,'$.a')");
        AssertNull("json_extract('{\"a\":1}', NULL)");
        AssertNull("json_extract('{\"a\":1}', '$.a', NULL)");
        AssertInt("json_extract('{\"a-b\":1}','$.a-b')", 1);
        AssertInt("json_extract('{\"a b\":1}','$.a b')", 1);
        AssertInt("json_extract('{\"a\":1}','$.\"a\"')", 1);
        AssertInt("json_extract('{\"a.b\":9}','$.\"a.b\"')", 9);
        AssertNull("json_extract('[1,2]','$.a')");
        AssertNull("json_extract('{\"a\":1}','$[0]')");
        AssertThrows("json_extract('bad','$.a')");
        AssertThrows("json_extract('{\"a\":1}','a')");
        AssertNull("json_extract('{\"a\":1}','$[-1]')");
        AssertNull("json_extract('{\"a\":1}','$[ 0 ]')");
        AssertText("json_extract('{}','$')", "{}");
        AssertText("json_extract('{\"a\":1}','$')", "{\"a\":1}");
        AssertInt("json_extract('{\"a\":{\"b\":[10,20]}}','$.a.b[1]')", 20);
        AssertInt("json_extract('[[1,2],[3,4]]','$[1][0]')", 3);
        AssertNull("json_extract('{\"a\":1}','$.a.b')");
        AssertThrows("json_extract('{\"a\":1}','$x')");
        AssertInt("json_extract(123,'$')", 123);
        AssertReal("json_extract('99999999999999999999','$')", 1e+20);
    }

    [Test]
    public void JsonAndDateTime_ArityErrors()
    {
        AssertThrows("json()");
        AssertThrows("json('a','b')");
        AssertThrows("json_valid()");
        AssertThrows("json_type('{}','$','$')");
    }

    // "now" and local-timezone functions are inherently time/environment dependent, so these are
    // verified structurally (shape + round-trip) rather than against a fixed oracle value.
    [Test]
    public void TimeDependentFunctions_AreWellFormed()
    {
        Scalar("date('now')").AsText().Should().MatchRegex(@"^-?\d{4}-\d{2}-\d{2}$");
        Scalar("time('now')").AsText().Should().MatchRegex(@"^\d{2}:\d{2}:\d{2}$");
        Scalar("datetime('now')").AsText().Should().MatchRegex(@"^-?\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
        Scalar("strftime('%Y','now')").AsText().Should().MatchRegex(@"^-?\d{4}$");

        var unixNow = Scalar("unixepoch('now')");
        unixNow.Kind.Should().Be(SqlValueKind.Integer);
        unixNow.AsInteger().Should().BeGreaterThan(1_600_000_000);

        var julian = Scalar("julianday('now')");
        julian.Kind.Should().Be(SqlValueKind.Real);
        julian.AsReal().Should().BeGreaterThan(2_400_000.0);

        // Applying 'utc' then 'localtime' (or the reverse) must round-trip to the original instant.
        Scalar("datetime('2023-06-15 12:00:00','utc','localtime')").AsText()
            .Should().MatchRegex(@"^-?\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
        Scalar("datetime('2023-06-15 12:00:00','localtime','utc')").AsText()
            .Should().Be("2023-06-15 12:00:00");
    }

    private static SqlValue Scalar(string expression)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT " + expression + ";");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void AssertText(string expression, string expected)
        => Scalar(expression).Should().Be(SqlValue.Text(expected), because: expression);

    private static void AssertInt(string expression, long expected)
        => Scalar(expression).Should().Be(SqlValue.Integer(expected), because: expression);

    private static void AssertNull(string expression)
        => Scalar(expression).Should().Be(SqlValue.Null, because: expression);

    private static void AssertReal(string expression, double expected)
    {
        var value = Scalar(expression);
        value.Kind.Should().Be(SqlValueKind.Real, because: expression);
        value.AsReal().Should().Be(expected, because: expression);
    }

    private static void AssertThrows(string expression)
        => Assert.Throws<EmbeddedSqlException>(() => Scalar(expression));
}
