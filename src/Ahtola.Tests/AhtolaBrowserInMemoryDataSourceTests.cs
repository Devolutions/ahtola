using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using Ahtola.Data.Sqlite.Browser;
using AwesomeAssertions;

#pragma warning disable CA1416

namespace Ahtola.Tests;

public sealed class AhtolaBrowserInMemoryDataSourceTests
{
    [Test]
    public void MemoryOptionsUseSharedMemoryWithoutOpfs()
    {
        using var options = new AhtolaBrowserOptions(":memory:");
        var connectionString = new SqliteConnectionStringBuilder(options.ConnectionString);

        options.IsInMemory.Should().BeTrue();
        options.IsEncrypted.Should().BeFalse();
        options.DatabasePath.Should().Be(":memory:");
        options.OwnedDirectory.Should().Be(":memory:");
        connectionString.Mode.Should().Be(SqliteOpenMode.Memory);
        connectionString.Cache.Should().Be(SqliteCacheMode.Shared);
        connectionString.Pooling.Should().BeFalse();
    }

    [Test]
    public void MemoryOptionsRejectStorageOnlySettings()
    {
        using var encryption = AhtolaBrowserEncryptionOptions.FromKey(
            AhtolaEncryptionCipher.Aes128Gcm,
            new byte[16]);

        new Action(() => new AhtolaBrowserOptions(":memory:", readOnly: true))
            .Should().Throw<ArgumentException>();
        new Action(() => new AhtolaBrowserOptions(":memory:", encryption: encryption))
            .Should().Throw<NotSupportedException>();
        new Action(() => new AhtolaBrowserOptions(":memory:", "owned"))
            .Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task ConnectionsFromOneMemoryDataSourceShareState()
    {
        await using var dataSource = new AhtolaBrowserDataSource(":memory:");
        await using var first = await dataSource.OpenConnectionAsync();
        await using var second = await dataSource.OpenConnectionAsync();
        await using (var create = first.CreateCommand())
        {
            create.CommandText = "CREATE TABLE probe(value INTEGER); INSERT INTO probe VALUES (42)";
            await create.ExecuteNonQueryAsync();
        }

        await using var query = second.CreateCommand();
        query.CommandText = "SELECT value FROM probe";

        (await query.ExecuteScalarAsync()).Should().Be(42L);
    }

    [Test]
    public async Task MemoryDataSourceSharesStateAcrossBothConnectionFacades()
    {
        await using var dataSource = new AhtolaBrowserDataSource(":memory:");
        await using (var sqlite = await dataSource.OpenConnectionAsync())
        {
            await using var create = sqlite.CreateCommand();
            create.CommandText = "CREATE TABLE probe(value INTEGER); INSERT INTO probe VALUES (84)";
            await create.ExecuteNonQueryAsync();
        }

        await using var ahtola = dataSource.CreateAhtolaConnection();
        await ahtola.OpenAsync();
        await using var query = ahtola.CreateCommand();
        query.CommandText = "SELECT value FROM probe";

        (await query.ExecuteScalarAsync()).Should().Be(84L);
    }

    [Test]
    public async Task MemoryDataSourceStillRejectsSynchronousExecution()
    {
        await using var dataSource = new AhtolaBrowserDataSource(":memory:");
        await using var connection = await dataSource.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        command.Invoking(static value => value.ExecuteScalar())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*ExecuteScalarAsync*");
    }

    [Test]
    public async Task MemoryDataSourcePreservesSharedMemoryIsolationGuards()
    {
        await using var dataSource = new AhtolaBrowserDataSource(":memory:");
        await using (var sqlite = await dataSource.OpenConnectionAsync())
        {
            sqlite.IsManagedSharedMemory.Should().BeTrue();
            await sqlite.Invoking(static value =>
                    value.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted).AsTask())
                .Should().ThrowAsync<NotSupportedException>();
        }

        await using var ahtola = dataSource.CreateAhtolaConnection();
        await ahtola.OpenAsync();
        await ahtola.Invoking(static value =>
                value.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted).AsTask())
            .Should().ThrowAsync<NotSupportedException>();
    }
}
