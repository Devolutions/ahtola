using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class ManagedModuloAndPrintfConformanceTests
{
    [Test]
    public void ModuloCoercesValuesAndPreservesSqliteResultTypes()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var values = ReadRow(
            connection,
            """
            SELECT
                10 + 11 % 3 * 2,
                38 % 10.35,
                38.43 % 13,
                0 % 12.0,
                '10' % '3',
                '10.0' % '3',
                '123abc' % 2,
                x'3130' % 3,
                'a' % 'a',
                183 % NULL,
                183 % 0,
                -9223372036854775808 % -1
            """);

        values.Should().Equal(
            SqlValue.Integer(14),
            SqlValue.Real(8),
            SqlValue.Real(12),
            SqlValue.Real(0),
            SqlValue.Integer(1),
            SqlValue.Real(1),
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Integer(0));
    }

    [Test]
    public void PrintfSupportsDocumentedVerbsWithDefaultFormatting()
    {
        using var connection = new EmbeddedDatabase().Connect();

        var value = ReadRow(
            connection,
            """
            SELECT printf(
                's=%s|null=%s|d=%d|i=%i|u=%u|x=%x|X=%X|o=%o|f=%f|e=%e|E=%E|g=%g|G=%G|c=%c|q=%q|Q=%Q|w=%w|blob=%s|%%',
                'text', NULL, '123abc', 3.9, -1, 255, 255, 8, 42.5, 23000000.0, 23000000.0,
                1234567.0, 1234567.0, 'hello', 'it''s', 'it''s', 'col"name', x'410042')
            """)[0];

        value.Should().Be(SqlValue.Text(
            "s=text|null=|d=123|i=3|u=18446744073709551615|x=ff|X=FF|o=10|f=42.500000"
            + "|e=2.300000e+07|E=2.300000E+07|g=1.23457e+06|G=1.23457E+06|c=h"
            + "|q=it''s|Q='it''s'|w=col\"\"name|blob=A|%"));

        ReadRow(connection, "SELECT printf()")[0].Should().Be(SqlValue.Null);
        ReadRow(connection, "SELECT printf(NULL)")[0].Should().Be(SqlValue.Null);
        ReadRow(connection, "SELECT printf(123)")[0].Should().Be(SqlValue.Text("123"));
        ReadRow(connection, "SELECT printf('%d|%s')")[0].Should().Be(SqlValue.Text("0|"));
    }

    [Test]
    public void PrintfAndFormatSupportBoundedStaticWidthPrecisionAndCoercions()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRow(
            connection,
            """
            SELECT format(
                'i=%+05d|u=%.4u|x=%08x|o=%-6.3o|f=%+010.2f|e=%.2e|g=%.3g|s=%8.3s|q=%7.3q|Q=%-8.2Q|w=%7.3w|c=%4.3c|blob=%5.3s',
                -42, 8, 255, 8, 3.14, 23000000.0, 123.456, 'hello', 'a''b''', 'abc', 'a"bc', 'z', x'4142434400')
            """)[0].Should().Be(SqlValue.Text(
            "i=-0042|u=0008|x=000000ff|o=010   |f=+000003.14|e=2.30e+07|g=123"
            + "|s=     hel|q=   a''b|Q='ab'    |w=   a\"\"b|c= zzz|blob=  ABC"));

        ReadRow(connection, "SELECT format('%s|%d|%f|%s', 1.5, '123abc', x'31322e35', x'410042')")[0]
            .Should().Be(SqlValue.Text("1.5|123|12.500000|A"));
        ReadRow(connection, "SELECT format('x%s|%Q|%q|%w', NULL, NULL, NULL, NULL)")[0]
            .Should().Be(SqlValue.Text("x|NULL|(NULL)|(NULL)"));
        ReadRow(connection, "SELECT format('%.0f|%.1f|%.2f|%.0e', 0.5, 0.25, 0.125, 2.5)")[0]
            .Should().Be(SqlValue.Text("1|0.3|0.13|3e+00"));
        ReadRow(connection, "SELECT format('|%10s|%.1s|%5.1s|', 'é', 'é', 'é')")[0]
            .Should().Be(SqlValue.Text("|        é|�|    �|"));
        ReadRow(connection, "SELECT format('')")[0].Should().Be(SqlValue.Null);
    }

    [Test]
    public void PrintfAppliesPrecisionToNullSentinelsAndUsesSqliteRealTextCoercion()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRow(
            connection,
            "SELECT printf('%.1Q|%.1q|%.1w|%5.1Q|%5.1q|%5.1w', NULL, NULL, NULL, NULL, NULL, NULL)")[0]
            .Should().Be(SqlValue.Text("N|(|(|    N|    (|    ("));
        ReadRow(
            connection,
            "SELECT printf('%s|%q|%Q|%w|%c', 1.2345678901234567, 1.2345678901234567, 1.2345678901234567, 1.2345678901234567, 1.2345678901234567)")[0]
            .Should().Be(SqlValue.Text(
                "1.23456789012346|1.23456789012346|'1.23456789012346'|1.23456789012346|1"));
    }

    [Test]
    public void PrintfBoundsUtf8EncodingForPrecisionAndOversizeOutput()
    {
        const int largeInputLength = 4_000_000;
        const long maximumExecutionAllocation = 1_000_000;
        var largeInput = new string('a', largeInputLength);
        using var connection = new EmbeddedDatabase().Connect();

        ReadRow(connection, "SELECT printf('%.3s', 'warm')");

        using (var prefix = connection.Prepare("SELECT printf('%.3s', ?)"))
        {
            prefix.Bind(1, SqlValue.Text(largeInput));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            prefix.Step().Should().Be(StatementStepResult.Row);

            (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore)
                .Should().BeLessThan(maximumExecutionAllocation);
            prefix.GetValue(0).Should().Be(SqlValue.Text("aaa"));
        }

        using var oversize = connection.Prepare("SELECT printf('%s', ?)");
        oversize.Bind(1, SqlValue.Text(largeInput));
        var oversizeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var exception = Assert.Throws<EmbeddedSqlException>(() => oversize.Step());

        exception!.Message.Should().Be("printf output exceeds 1000000 characters.");
        (GC.GetAllocatedBytesForCurrentThread() - oversizeAllocatedBefore)
            .Should().BeLessThan(maximumExecutionAllocation);
    }

    [Test]
    public void PrintfSupportsDynamicWidthPrecisionAndLengthModifiers()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRow(
            connection,
            "SELECT printf('%*.*f|%*s|%.*s|%ld|%lld', 10, 2, 3.14, -5, 'x', 3, 'hello', 4, 5)")[0]
            .Should().Be(SqlValue.Text("      3.14|x    |hel|4|5"));
    }

    [Test]
    public void PrintfSupportsAlternateCommaAndCharacterFlags()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRow(
            connection,
            """
            SELECT printf(
                '%#x|%#X|%#o|%#.0f|%#g|%,d|%,.2f|%!5.1s|%#08x|%#08o|%0,10d|%!g|%-05d|%,x|%,o',
                255, 255, 8, 1.0, 1.2, 123456789, 1234567.25, 'éx', 255, 8, 1234567, 1.0, 12,
                123456789, 123456789)
            """)[0].AsText().Should().Be(
            "0xff|0XFF|010|1.|1.20000|123,456,789|1,234,567.25|    é"
                + "|0x000000ff|000000010|0,001,234,567|1.0|00012|75bcd15|726746425");
    }

    [Test]
    public void PrintfSupportsPointerOrdinalCountAndControlEscaping()
    {
        using var connection = new EmbeddedDatabase().Connect();

        ReadRow(
            connection,
            """
            SELECT printf(
                '%p|%#p|%z|%n%d|%r|%r|%r|%r|%#q|%#Q',
                255, 255, 'abc', 123, 1, 2, 3, 11,
                x'01610A',
                x'01610A')
            """)[0].Should().Be(SqlValue.Text(
            "FF|0xFF|abc|123|1st|2nd|3rd|11th|\\u0001a\\u000a|unistr('\\u0001a\\u000a')"));
    }

    private static SqlValue[] ReadRow(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);

        var values = new SqlValue[statement.GetColumnCount()];
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
            values[ordinal] = statement.GetValue(ordinal);

        statement.Step().Should().Be(StatementStepResult.Done);
        return values;
    }
}
