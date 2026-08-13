using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public sealed class TursoAggregateStepParityTests
{
    [Test]
    public void SumIntegerOverflowStopsTheCompiledAggregateAtTheOverflowingRow()
    {
        var observed = new List<long>();
        var database = new EmbeddedDatabase();
        database.RegisterScalarFunction("observe", 1, arguments =>
        {
            var value = arguments[0].AsInteger();
            observed.Add(value);
            return arguments[0];
        });

        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(x INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (9223372036854775807), (1), (2);");

        using var statement = connection.Prepare("SELECT sum(observe(x)) FROM values_table;");
        Assert.Throws<EmbeddedSqlException>(() => statement.Step())
            .Message.Should().Be("integer overflow");
        observed.Should().Equal(9223372036854775807, 1);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
