using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.Data.Sqlite.Browser;

#pragma warning disable CA1416

namespace Ahtola.Tests;

public sealed class AhtolaBrowserDataSourceTests
{
    [Test]
    public void OptionsNormalizePathsAndBuildManagedConnectionString()
    {
        var options = new AhtolaBrowserOptions(
            @"applications\inventory\data.db",
            @"applications\inventory",
            sharedBufferSize: 128 * 1024,
            readOnly: true);

        options.DatabasePath.Should().Be("applications/inventory/data.db");
        options.OwnedDirectory.Should().Be("applications/inventory");
        options.SharedBufferSize.Should().Be(128 * 1024);
        options.IsReadOnly.Should().BeTrue();

        var connectionOptions = new SqliteConnectionStringBuilder(options.ConnectionString);
        connectionOptions.DataSource.Should().Be(options.DatabasePath);
        connectionOptions.Mode.Should().Be(SqliteOpenMode.ReadOnly);
        connectionOptions.LocalProvider.Should().Be(AhtolaLocalProvider.Managed);
        connectionOptions.Pooling.Should().BeFalse();
    }

    [TestCase("data.db", "owned")]
    [TestCase("other/data.db", "owned")]
    [TestCase("owned/../data.db", "owned")]
    [TestCase("/owned/data.db", "owned")]
    [TestCase("owned/data.db", "owned/")]
    public void OptionsRejectPathsOutsideNormalizedOwnedDirectory(
        string databasePath,
        string ownedDirectory)
    {
        var action = () => new AhtolaBrowserOptions(databasePath, ownedDirectory);

        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void OptionsRequireWorkerBufferMinimum()
    {
        var action = () => new AhtolaBrowserOptions(
            "owned/data.db",
            "owned",
            sharedBufferSize: 64 * 1024 - 1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void OptionsEnforceWorkerBufferMaximum()
    {
        var action = () => new AhtolaBrowserOptions(
        "owned/data.db",
        "owned",
        sharedBufferSize: 64 * 1024 * 1024 + 1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task FactoryCreatesTypedClosedConnectionsAndGuardsSyncOpen()
    {
        await using var source = new AhtolaBrowserDataSource(
            new AhtolaBrowserOptions("owned/data.db", "owned"));
        await using var sqliteConnection = source.CreateConnection();
        await using var ahtolaConnection = source.CreateAhtolaConnection();

        sqliteConnection.Should().BeOfType<SqliteConnection>();
        sqliteConnection.State.Should().Be(ConnectionState.Closed);
        sqliteConnection.DataSource.Should().Be("owned/data.db");
        sqliteConnection.ConnectionString.Should().Be(source.ConnectionString);
        ahtolaConnection.State.Should().Be(ConnectionState.Closed);
        ahtolaConnection.DataSource.Should().Be("owned/data.db");
        ahtolaConnection.ConnectionString.Should().Be(source.ConnectionString);

        source.Invoking(static value => value.OpenConnection())
            .Should().Throw<PlatformNotSupportedException>();
        sqliteConnection.Invoking(static value => value.Open())
            .Should().Throw<PlatformNotSupportedException>();
        ahtolaConnection.Invoking(static value => value.Open())
            .Should().Throw<PlatformNotSupportedException>();

        sqliteConnection.CreateCommand()
            .Invoking(static command => command.ExecuteNonQuery())
            .Should().Throw<PlatformNotSupportedException>();
        ahtolaConnection.CreateCommand()
            .Invoking(static command => command.ExecuteNonQuery())
            .Should().Throw<PlatformNotSupportedException>();
    }

    [Test]
    public async Task FactorySyncDisposalFailsClearly()
    {
        var source = new AhtolaBrowserDataSource("owned/data.db");

        ((DbDataSource)source).Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>();

        await source.DisposeAsync();
    }
}

#pragma warning restore CA1416
