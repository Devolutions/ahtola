using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class CoreManagedLocalAdapterLifecycleTests
{
    [Test]
    public void CoreManagedAdapterPreservesMetadataBindingsAndDeferredClearLifecycle()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();
        using var statement = connection.Prepare("SELECT ?1 AS value;");

        statement.ParameterCount.Should().Be(1);
        statement.GetParameterName(1).Should().Be("?1");
        statement.GetColumnCount().Should().Be(1);
        statement.GetColumnName(0).Should().Be("value");

        statement.Bind(1, SqlValue.Integer(7));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(7);

        statement.ClearBindings();
        statement.GetValue(0).AsInteger().Should().Be(7);
        statement.Reset();

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        statement.Reset();

        statement.Bind(1, SqlValue.Integer(9));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(9);
    }
}
