using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedDataReaderParityTests
{
    [Test]
    public void SqliteManagedReaderReportsActualRowsAndCopiesOnlyAvailableValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        connection.ExecuteNonQuery("CREATE TABLE data(id INTEGER, value TEXT); INSERT INTO data VALUES (1, 'one');");

        using (var emptyCommand = connection.CreateCommand())
        {
            emptyCommand.CommandText = "SELECT value FROM data WHERE id = 2;";
            using var emptyReader = emptyCommand.ExecuteReader();
            emptyReader.HasRows.Should().BeFalse();
            emptyReader.Read().Should().BeFalse();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, value FROM data;";
        using var reader = command.ExecuteReader();
        reader.HasRows.Should().BeTrue();
        reader.Read().Should().BeTrue();

        var values = new object[1];
        reader.GetValues(values).Should().Be(1);
        values[0].Should().Be(1L);
    }

    [Test]
    public void AhtolaManagedReaderCopiesOnlyAvailableValues()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 AS id, 'one' AS value;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();

        var values = new object[1];
        reader.GetValues(values).Should().Be(1);
        values[0].Should().Be(1L);
    }
}
