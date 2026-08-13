using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class TursoIntegerMathParityTests
{
    [TestCase("gcd(12, 8)", 4)]
    [TestCase("gcd(0, 7)", 7)]
    [TestCase("gcd(7, 0)", 7)]
    [TestCase("gcd(0, 0)", 0)]
    [TestCase("gcd(-12, 8)", 4)]
    [TestCase("gcd(-12, -8)", 4)]
    [TestCase("lcm(4, 6)", 12)]
    [TestCase("lcm(0, 5)", 0)]
    [TestCase("lcm(5, 0)", 0)]
    [TestCase("lcm(-4, 6)", 12)]
    [TestCase("lcm(-4, -6)", 12)]
    public void GcdAndLcmMatchTursosBasicVectors(string expression, long expected)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, $"SELECT {expression};").Should().Be(SqlValue.Integer(expected));
    }

    [TestCase("gcd(NULL, 7)")]
    [TestCase("gcd(7, NULL)")]
    [TestCase("lcm(NULL, 7)")]
    [TestCase("lcm(7, NULL)")]
    [TestCase("gcd('12.0', 8)")]
    [TestCase("lcm(X'01', 8)")]
    public void GcdAndLcmReturnNullForTursosInvalidOperands(string expression)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, $"SELECT {expression};").Should().Be(SqlValue.Null);
    }

    [Test]
    public void GcdAndLcmTruncateFiniteRealOperandsLikeTurso()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, "SELECT gcd(12.9, 8.1);").Should().Be(SqlValue.Integer(4));
        Scalar(connection, "SELECT lcm(4.9, 6.1);").Should().Be(SqlValue.Integer(12));
    }

    [TestCase("gcd(-9223372036854775808, 0)")]
    [TestCase("gcd(-9223372036854775808, -9223372036854775808)")]
    [TestCase("lcm(9223372036854775807, 3)")]
    [TestCase("lcm(-9223372036854775808, 1)")]
    public void GcdAndLcmReportTursosOverflowCases(string expression)
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, $"SELECT {expression};"))
            .Message.Should().Be("integer overflow");
    }

    [Test]
    public void GcdViewSurvivesFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "gcd-view.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE values_table(left_value, right_value);");
            Execute(connection, "INSERT INTO values_table VALUES (12, 8);");
            Execute(connection, "CREATE VIEW divisors AS SELECT gcd(left_value, right_value) AS value FROM values_table;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connectionAfterReopen = reopened.Connect();
        Scalar(connectionAfterReopen, "SELECT value FROM divisors;").Should().Be(SqlValue.Integer(4));
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
