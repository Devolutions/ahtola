using System.Reflection;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedBackupPreflightTests
{
    [TestCase(nameof(Data.Sqlite.Properties.Resources.ManagedBackupAttachedDatabasesNotSupported))]
    [TestCase(nameof(Data.Sqlite.Properties.Resources.ManagedBackupSameConnectionNotSupported))]
    [TestCase(nameof(Data.Sqlite.Properties.Resources.ManagedBackupDestinationMustBeEmpty))]
    public void ManagedBackupLegacyResourcePropertiesRemainPublic(string propertyName)
    {
        var property = typeof(Data.Sqlite.Properties.Resources).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Static);

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));
        property.GetMethod.Should().NotBeNull();
        property.GetValue(null).Should().BeOfType<string>();
    }

    [Test]
    public void ManagedBackupRejectsClosedMixedProviderDestinationBeforeOpeningIt()
    {
        using var source = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var destination = new SqliteConnection("Data Source=:memory:;Local Provider=Native");
        source.Open();
        source.ExecuteNonQuery("CREATE TABLE source_data(value TEXT); INSERT INTO source_data VALUES ('source');");

        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage(Data.Sqlite.Properties.Resources.ManagedBackupMixedProvidersNotSupported);

        destination.State.Should().Be(System.Data.ConnectionState.Closed);
        source.ExecuteScalar<string>("SELECT value FROM source_data;").Should().Be("source");
    }
}
