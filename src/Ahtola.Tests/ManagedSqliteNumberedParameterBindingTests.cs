using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedSqliteNumberedParameterBindingTests
{
    [Test]
    public void ManagedFacadeBindsContiguousNumberedParametersByExactName()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ?2, :name, ?1, ?2;";
        command.Parameters.AddWithValue("?1", "first");
        command.Parameters.AddWithValue("name", "named");
        command.Parameters.AddWithValue("?2", "second");

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("second");
        reader.GetString(1).Should().Be("named");
        reader.GetString(2).Should().Be("first");
        reader.GetString(3).Should().Be("second");
        reader.Read().Should().BeFalse();
    }

    [Test]
    public void ManagedFacadeBindsUnnamedParametersOnlyAfterNumberedSlots()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ?1, ?2, :name, ?;";
        command.Parameters.AddWithValue(null, "unnamed");
        command.Parameters.AddWithValue("?2", "second");
        command.Parameters.AddWithValue("name", "named");
        command.Parameters.AddWithValue("?1", "first");

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("first");
        reader.GetString(1).Should().Be("second");
        reader.GetString(2).Should().Be("named");
        reader.GetString(3).Should().Be("unnamed");
        reader.Read().Should().BeFalse();
    }

    [Test]
    public void ManagedFacadeRejectsNumberedParameterGapsInsteadOfGuessingPositionalSlots()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ?2, ?;";
        command.Parameters.AddWithValue("?2", "numbered");
        command.Parameters.AddWithValue(null, "unnamed");

        Assert.Throws<NotSupportedException>(() => command.ExecuteScalar())!
            .Message.Should().Be(
                "Numbered parameters with gaps or preceding unnamed parameters are not supported by Local Provider=Managed.");
    }

    [Test]
    public void ManagedFacadeReportsUnboundNumberedParametersByName()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ?1, ?2;";
        command.Parameters.AddWithValue("?1", "first");

        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar())!
            .Message.Should().Be("Missing parameter values for ?2.");
    }
}
