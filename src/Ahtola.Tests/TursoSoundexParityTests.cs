using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class TursoSoundexParityTests
{
    [TestCase("Pfister", "P236")]
    [TestCase("husobee", "H210")]
    [TestCase("Tymczak", "T522")]
    [TestCase("Ashcraft", "A261")]
    [TestCase("Robert", "R163")]
    [TestCase("Rupert", "R163")]
    [TestCase("Rubin", "R150")]
    [TestCase("Kant", "K530")]
    [TestCase("Knuth", "K530")]
    [TestCase("x", "X000")]
    public void SoundexMatchesTursosUpstreamVectors(string value, string expected)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, $"SELECT soundex('{value}');").Should().Be(SqlValue.Text(expected));
    }

    [TestCase("NULL")]
    [TestCase("123")]
    [TestCase("'abc-123'")]
    [TestCase("'闪电五连鞭'")]
    [TestCase("''")]
    public void SoundexReturnsTursosSentinelForNonAsciiAndNonTextValues(string value)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, $"SELECT soundex({value});").Should().Be(SqlValue.Text("?000"));
    }

    [Test]
    public void SoundexReportsItsCanonicalArityDiagnostic()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT soundex();"))
            .Message.Should().Be("wrong number of arguments to function soundex()");
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }
}
