using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class TursoStringReverseParityTests
{
    [Test]
    public void StringReverseReversesUnicodeScalarsAndAcceptsTursosAlias()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, "SELECT string_reverse('A😀B𐐷');")
            .Should().Be(SqlValue.Text("𐐷B😀A"));
        Scalar(connection, "SELECT reverse('abc');")
            .Should().Be(SqlValue.Text("cba"));
    }

    [Test]
    public void StringReverseCoercesValuesAndPropagatesNull()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, "SELECT string_reverse(120.5);").Should().Be(SqlValue.Text("5.021"));
        Scalar(connection, "SELECT string_reverse(X'414243');").Should().Be(SqlValue.Text("CBA"));
        Scalar(connection, "SELECT string_reverse(NULL);").Should().Be(SqlValue.Null);
    }

    [Test]
    public void StringReverseReportsTursosCanonicalArityDiagnostic()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT string_reverse();"))
            .Message.Should().Be("wrong number of arguments to function string_reverse()");
    }

    [Test]
    public void StringReverseViewSurvivesFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "string-reverse-view.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE values_table(value);");
            Execute(connection, "INSERT INTO values_table VALUES ('A😀B');");
            Execute(connection, "CREATE VIEW reversed_values AS SELECT string_reverse(value) AS value FROM values_table;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connectionAfterReopen = reopened.Connect();
        Scalar(connectionAfterReopen, "SELECT value FROM reversed_values;")
            .Should().Be(SqlValue.Text("B😀A"));
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
