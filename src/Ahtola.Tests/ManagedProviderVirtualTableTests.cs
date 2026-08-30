using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ManagedProviderVirtualTableTests
{
    [Test]
    public void ShippedProviderSelectsFromFts5VirtualTables()
    {
        using var connection = OpenConnection();
        connection.ExecuteNonQuery("""
            CREATE VIRTUAL TABLE documents USING fts5(title, body);
            INSERT INTO documents(rowid, title, body)
            VALUES (1, 'Orchid', 'purple flower'), (2, 'Other', 'plain text');
            """);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rowid, title FROM documents WHERE documents MATCH 'orchid' ORDER BY rowid;";
        using var reader = command.ExecuteReader();

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetString(1).Should().Be("Orchid");
        reader.Read().Should().BeFalse();
    }

    [Test]
    public void ShippedProviderSelectsFromRTreeVirtualTables()
    {
        using var connection = OpenConnection();
        connection.ExecuteNonQuery("""
            CREATE VIRTUAL TABLE boxes USING rtree(id, min_x, max_x, min_y, max_y);
            INSERT INTO boxes VALUES (1, 0, 4, 0, 4), (2, 10, 12, 10, 12);
            """);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id FROM boxes WHERE min_x <= 2 AND max_x >= 2 ORDER BY id;";
        using var reader = command.ExecuteReader();

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.Read().Should().BeFalse();
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }
}
