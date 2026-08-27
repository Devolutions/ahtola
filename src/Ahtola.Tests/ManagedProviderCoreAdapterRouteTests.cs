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

    [Test]
    public void ManagedAhtolaConnectionAppliesConnectionPragmas()
    {
        using var connection = new AhtolaConnection(
            "Data Source=:memory:;Local Provider=Managed;Foreign Keys=True;Recursive Triggers=True");
        connection.Open();

        ExecuteScalar(connection, "PRAGMA foreign_keys;").Should().Be(1);
        ExecuteScalar(connection, "PRAGMA recursive_triggers;").Should().Be(1);

        connection.ExecuteNonQuery("CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        connection.ExecuteNonQuery("CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        connection.Invoking(static current => current.ExecuteNonQuery("INSERT INTO child VALUES (1);"))
            .Should()
            .Throw<AhtolaException>();

        using var sqlite = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Foreign Keys=True;Recursive Triggers=True");
        sqlite.Open();
        sqlite.ExecuteScalar<long>("PRAGMA foreign_keys;").Should().Be(1);
        sqlite.ExecuteScalar<long>("PRAGMA recursive_triggers;").Should().Be(1);
    }

    [Test]
    public void ManagedAhtolaConnectionAppliesExplicitFalseAfterPoolReset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-options-{Guid.NewGuid():N}.db");
        try
        {
            using (var enabled = new AhtolaConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=True;Foreign Keys=True;Recursive Triggers=True"))
            {
                enabled.Open();
                ExecuteScalar(enabled, "PRAGMA foreign_keys;").Should().Be(1);
                ExecuteScalar(enabled, "PRAGMA recursive_triggers;").Should().Be(1);
            }

            using var disabled = new AhtolaConnection(
                $"Data Source={path};Local Provider=Managed;Pooling=True;Foreign Keys=False;Recursive Triggers=False");
            disabled.Open();

            ExecuteScalar(disabled, "PRAGMA foreign_keys;").Should().Be(0);
            ExecuteScalar(disabled, "PRAGMA recursive_triggers;").Should().Be(0);
        }
        finally
        {
            AhtolaConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private static long ExecuteScalar(AhtolaConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected {instance.GetType().Name}.{fieldName}.");
        return field.GetValue(instance);
    }
}
