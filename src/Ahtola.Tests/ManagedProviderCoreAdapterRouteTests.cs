using System.Reflection;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedProviderCoreAdapterRouteTests
{
    [Test]
    public void ManagedProvidersOwnCoreAdaptersWithoutRawManagedHandles()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        GetPrivateField(connection, "_managedDatabase").Should().BeAssignableTo<IManagedDatabaseAdapter>();
        GetPrivateField(connection, "_nativeDatabase").Should().BeNull();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT $value;";
            command.Parameters.Add(new AhtolaParameter("$value", 42L));
            command.ExecuteScalar().Should().Be(42L);
        }

        using var sqlite = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        sqlite.CreateFunction<long, long>("core_adapter_increment", static value => value + 1);
        sqlite.CreateAggregate<long, long>("core_adapter_total", 0L, static (total, value) => total + value);
        sqlite.CreateCollation("core_adapter_reverse", static (left, right) => -string.CompareOrdinal(left, right));
        sqlite.Open();

        GetPrivateField(sqlite, "_managedDatabase").Should().BeAssignableTo<IManagedDatabaseAdapter>();
        GetPrivateField(sqlite, "_database").Should().BeNull();

        using (var command = sqlite.CreateCommand())
        {
            command.CommandText = "SELECT core_adapter_increment($value);";
            command.Parameters.AddWithValue("$value", 41L);
            command.ExecuteScalar().Should().Be(42L);
        }

        sqlite.ExecuteScalar<long>("SELECT core_adapter_total(value) FROM (SELECT 1 AS value UNION ALL SELECT 2);")
            .Should().Be(3);

        sqlite.ExecuteScalar<string>("SELECT value FROM (SELECT 'a' AS value UNION ALL SELECT 'b') ORDER BY value COLLATE core_adapter_reverse LIMIT 1;")
            .Should().Be("b");

        using (var command = sqlite.CreateCommand())
        {
            command.CommandText = "SELECT 1;";
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(1);
        }

        sqlite.ExecuteScalar<long>("SELECT 2;").Should().Be(2);

        new AhtolaConnectionStringBuilder("Local Provider=Native").LocalProvider.Should().Be(AhtolaLocalProvider.Native);
        new SqliteConnectionStringBuilder("Local Provider=Native").LocalProvider.Should().Be(AhtolaLocalProvider.Native);
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected {instance.GetType().Name}.{fieldName}.");
        return field.GetValue(instance);
    }
}
