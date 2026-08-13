using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class TursoUnistrParityTests
{
    [TestCase(@"'\u0041'", "A")]
    [TestCase(@"'\0041'", "A")]
    [TestCase(@"'\+01F600'", "😀")]
    [TestCase(@"'\U0001F600'", "😀")]
    [TestCase(@"'a\\b'", @"a\b")]
    [TestCase(@"'hi \u0041 \U0001F600'", "hi A 😀")]
    public void UnistrDecodesTursosEscapeForms(string source, string expected)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, $"SELECT unistr({source});").Should().Be(SqlValue.Text(expected));
    }

    [TestCase(@"\uD83D")]
    [TestCase(@"\U00110000")]
    [TestCase(@"\q")]
    [TestCase(@"\u00")]
    [TestCase(@"\u00GG")]
    [TestCase(@"\+01FG00")]
    [TestCase(@"\U0001F6GG")]
    public void UnistrRejectsInvalidUnicodeEscapes(string source)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, $"SELECT unistr('{source}');"))
            .Message.Should().Be("invalid Unicode escape");
    }

    [Test]
    public void UnistrQuoteMatchesTursosControlAndNulHandling()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, "SELECT unistr_quote(char(97, 9, 92, 98));")
            .Should().Be(SqlValue.Text(@"unistr('a\u0009\\b')"));
        Scalar(connection, "SELECT unistr_quote(char(97, 0, 9));")
            .Should().Be(SqlValue.Text("'a'"));
        Scalar(connection, "SELECT unistr_quote(X'DEADBEEF');")
            .Should().Be(SqlValue.Text("X'DEADBEEF'"));
    }

    [Test]
    public void UnistrReportsCanonicalArityDiagnostics()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT unistr();"))
            .Message.Should().Be("wrong number of arguments to function unistr()");
        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT unistr_quote();"))
            .Message.Should().Be("wrong number of arguments to function unistr_quote()");
    }

    [Test]
    public void UnistrViewSurvivesFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "unistr-view.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, @"CREATE VIEW decoded AS SELECT unistr('\U0001F600') AS value;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connectionAfterReopen = reopened.Connect();
        Scalar(connectionAfterReopen, "SELECT value FROM decoded;").Should().Be(SqlValue.Text("😀"));
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
