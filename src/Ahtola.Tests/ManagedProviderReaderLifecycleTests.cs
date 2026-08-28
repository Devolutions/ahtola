using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ManagedProviderReaderLifecycleTests
{
    [Test]
    public void ClosingAndReopeningManagedConnectionPermanentlyClosesActiveAhtolaReader()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();

        connection.Close();
        connection.Open();

        reader.IsClosed.Should().BeTrue();
        reader.Invoking(static value => value.Read()).Should().Throw<InvalidOperationException>();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);").Should().Be(0);
    }

    [Test]
    public void ManagedCommandCanBeReusedAfterItsReaderIsExhaustedAndDisposed()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(value INTEGER);");
        connection.ExecuteNonQuery("INSERT INTO data VALUES (1), (2);");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM data ORDER BY value;";

        using (var first = command.ExecuteReader())
        {
            first.Read().Should().BeTrue();
            first.GetInt64(0).Should().Be(1);
            first.Read().Should().BeTrue();
            first.GetInt64(0).Should().Be(2);
            first.Read().Should().BeFalse();
        }

        using var second = command.ExecuteReader();
        second.Read().Should().BeTrue();
        second.GetInt64(0).Should().Be(1);
        second.Read().Should().BeTrue();
        second.GetInt64(0).Should().Be(2);
        second.Read().Should().BeFalse();
    }
}
