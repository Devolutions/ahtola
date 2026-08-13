using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class TursoStringFunctionParityTests
{
    [TestCase("SELECT repeat('ab', 3);", "ababab")]
    [TestCase("SELECT repeat('x', 0);", "")]
    [TestCase("SELECT repeat('x', -1);", "")]
    [TestCase("SELECT repeat(12, 2);", "1212")]
    [TestCase("SELECT repeat('x', 'not-a-number');", "")]
    [TestCase("SELECT lpad('abc', 6);", "   abc")]
    [TestCase("SELECT rpad('abc', 6);", "abc   ")]
    [TestCase("SELECT lpad('abc', 6, 'xy');", "xyxabc")]
    [TestCase("SELECT rpad('abc', 6, 'xy');", "abcxyx")]
    [TestCase("SELECT lpad('abcdef', 3);", "abc")]
    [TestCase("SELECT rpad('abcdef', 3);", "abc")]
    [TestCase("SELECT lpad('aéc', 4);", " aéc")]
    [TestCase("SELECT rpad('aéc', 4);", "aéc ")]
    [TestCase("SELECT lpad(12, 4, 0);", "0012")]
    [TestCase("SELECT rpad(12, 4, 0);", "1200")]
    [TestCase("SELECT lpad('abc', 10, '');", "abc")]
    [TestCase("SELECT rpad('abc', 10, '');", "abc")]
    [TestCase("SELECT chr(65, 66);", "AB")]
    [TestCase("SELECT if(1, 'yes', 'no');", "yes")]
    public void StringFunctionsMatchTursoCharacterAndCoercionSemantics(string sql, string expected)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, sql).Should().Be(SqlValue.Text(expected));
    }

    [TestCase("SELECT char_length('a😀b');", 3L)]
    [TestCase("SELECT character_length('a😀b');", 3L)]
    [TestCase("SELECT strpos('alphabet', 'pha');", 3L)]
    public void StringFunctionAliasesMatchTursoIntegerResults(string sql, long expected)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, sql).Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("SELECT repeat(NULL, 3);")]
    [TestCase("SELECT repeat('x', NULL);")]
    [TestCase("SELECT repeat('x', X'00');")]
    [TestCase("SELECT lpad(NULL, 3);")]
    [TestCase("SELECT lpad('x', NULL);")]
    [TestCase("SELECT lpad('x', 3, NULL);")]
    [TestCase("SELECT rpad('x', X'00');")]
    public void StringFunctionsReturnNullForNullAndBlobLengthArguments(string sql)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, sql).Kind.Should().Be(SqlValueKind.Null);
    }

    [TestCase("SELECT repeat('x');", "repeat")]
    [TestCase("SELECT repeat('x', 2, 3);", "repeat")]
    [TestCase("SELECT lpad('x');", "lpad")]
    [TestCase("SELECT lpad('x', 2, '0', 'extra');", "lpad")]
    [TestCase("SELECT rpad('x');", "rpad")]
    [TestCase("SELECT rpad('x', 2, '0', 'extra');", "rpad")]
    public void StringFunctionsRejectInvalidArity(string sql, string functionName)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, sql))!
            .Message.Should().Be($"wrong number of arguments to function {functionName}()");
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
