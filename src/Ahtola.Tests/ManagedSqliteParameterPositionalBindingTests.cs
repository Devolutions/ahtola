using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedSqliteParameterPositionalBindingTests
{
    [Test]
    public void ManagedFacadeBindsUnnamedParametersByPositionalSlots()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT $name, ?, ?, $name;";
        command.Parameters.AddWithValue(null, "first");
        command.Parameters.AddWithValue("name", "named");
        command.Parameters.AddWithValue(null, "last");

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("named");
        reader.GetString(1).Should().Be("first");
        reader.GetString(2).Should().Be("last");
        reader.GetString(3).Should().Be("named");
        reader.Read().Should().BeFalse();
    }
}
