using System.Data;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class BrowserAsyncOnlyLifecycleTests
{
    [Test]
    public async Task SqliteConnectionSyncCloseAndDisposeRemainRecoverable()
    {
        var connection = CreateSqliteConnection();
        await connection.OpenAsync();

        connection.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*CloseAsync*");
        connection.State.Should().Be(ConnectionState.Open);
        connection.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        connection.State.Should().Be(ConnectionState.Open);

        await connection.DisposeAsync();

        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Test]
    public async Task AhtolaConnectionSyncCloseAndDisposeRemainRecoverable()
    {
        var connection = CreateAhtolaConnection();
        await connection.OpenAsync();

        connection.Invoking(static value => value.Close())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*CloseAsync*");
        connection.State.Should().Be(ConnectionState.Open);
        connection.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        connection.State.Should().Be(ConnectionState.Open);

        await connection.DisposeAsync();

        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Test]
    public async Task SqliteTransactionSyncOperationsDoNotTerminalizeBrowserTransaction()
    {
        await using var connection = CreateSqliteConnection();
        await connection.OpenAsync();
        connection.Invoking(static value => value.BeginTransaction())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*BeginTransactionAsync*");

        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        transaction.Invoking(static value => value.Rollback())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*RollbackAsync*");
        transaction.IsCompleted.Should().BeFalse();
        connection.Transaction.Should().BeSameAs(transaction);
        transaction.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        transaction.IsCompleted.Should().BeFalse();

        await transaction.DisposeAsync();

        transaction.IsCompleted.Should().BeTrue();
        connection.Transaction.Should().BeNull();
    }

    [Test]
    public async Task AhtolaTransactionSyncOperationsDoNotTerminalizeBrowserTransaction()
    {
        await using var connection = CreateAhtolaConnection();
        await connection.OpenAsync();
        connection.Invoking(static value => value.BeginTransaction())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*BeginTransactionAsync*");

        var transaction = (global::Ahtola.AhtolaTransaction)await connection.BeginTransactionAsync();
        transaction.Invoking(static value => value.Rollback())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*RollbackAsync*");
        transaction.IsCompleted.Should().BeFalse();
        connection.Transaction.Should().BeSameAs(transaction);
        transaction.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        transaction.IsCompleted.Should().BeFalse();

        await transaction.DisposeAsync();

        transaction.IsCompleted.Should().BeTrue();
        connection.Transaction.Should().BeNull();
    }

    [Test]
    public async Task SqliteReaderAsyncDisposalHonorsCloseConnection()
    {
        var connection = CreateSqliteConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var explicitlyClosed = await command.ExecuteReaderAsync();

        await explicitlyClosed.CloseAsync();

        explicitlyClosed.IsClosed.Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Open);
        var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        reader.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        reader.IsClosed.Should().BeFalse();
        connection.State.Should().Be(ConnectionState.Open);

        await reader.DisposeAsync();

        reader.IsClosed.Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Closed);
        await connection.DisposeAsync();
    }

    [Test]
    public async Task AhtolaReaderAsyncDisposalHonorsCloseConnection()
    {
        var connection = CreateAhtolaConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var explicitlyClosed = await command.ExecuteReaderAsync();

        await explicitlyClosed.CloseAsync();

        explicitlyClosed.IsClosed.Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Open);
        var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        reader.Invoking(static value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*DisposeAsync*");
        reader.IsClosed.Should().BeFalse();
        connection.State.Should().Be(ConnectionState.Open);

        await reader.DisposeAsync();

        reader.IsClosed.Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Closed);
        await connection.DisposeAsync();
    }

    [Test]
    public void BrowserDataSourceConnectionsCannotOverrideStorageConfiguration()
    {
        using var sqlite = CreateSqliteConnection();
        using var ahtola = CreateAhtolaConnection();
        var codec = new IdentityPageCodec();

        sqlite.Invoking(static value => value.ConnectionString = "Data Source=other.db")
            .Should().Throw<InvalidOperationException>();
        sqlite.Invoking(value => value.PageCodec = codec)
            .Should().Throw<InvalidOperationException>();
        ahtola.Invoking(static value => value.ConnectionString = "Data Source=other.db")
            .Should().Throw<InvalidOperationException>();
        ahtola.Invoking(value => value.PageCodec = codec)
            .Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task ConnectionOwnedReaderCloseDoesNotExecuteTrailingSql()
    {
        await using var connection = CreateSqliteConnection();
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE data(value INTEGER)";
            await create.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1; INSERT INTO data VALUES (42)";
        var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        await ((IConnectionOwnedReader)reader).CloseFromConnectionAsync();

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM data";
        (await count.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public async Task SqliteBatchReaderUsesAsyncTransitionsAndCloseConnection()
    {
        var connection = CreateSqliteConnection();
        await connection.OpenAsync();
        await using var batch = new SqliteBatch(connection);
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT 1"));
        batch.BatchCommands.Add(new SqliteBatchCommand("SELECT 2"));
        var reader = await batch.ExecuteReaderAsync(
            CommandBehavior.CloseConnection,
            CancellationToken.None);

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        (await reader.NextResultAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        await reader.DisposeAsync();

        reader.IsClosed.Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Closed);
        await connection.DisposeAsync();
    }

    [Test]
    public async Task ConnectionOwnedBatchReaderCloseDoesNotExecuteTrailingSql()
    {
        await using var connection = CreateSqliteConnection();
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE data(value INTEGER)";
            await create.ExecuteNonQueryAsync();
        }

        await using var batch = new SqliteBatch(connection);
        batch.BatchCommands.Add(new SqliteBatchCommand(
            "SELECT 1; INSERT INTO data VALUES (42)"));
        var reader = await batch.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        await ((IConnectionOwnedReader)reader).CloseFromConnectionAsync();

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM data";
        (await count.ExecuteScalarAsync()).Should().Be(0L);
        await reader.DisposeAsync();
    }

    [Test]
    public async Task AhtolaBatchReaderUsesAsyncTransitionsAndCloseConnection()
    {
        var connection = CreateAhtolaConnection();
        await connection.OpenAsync();
        await using var batch = new global::Ahtola.AhtolaBatch(connection);
        batch.BatchCommands.Add(new global::Ahtola.AhtolaBatchCommand("SELECT 1"));
        batch.BatchCommands.Add(new global::Ahtola.AhtolaBatchCommand("SELECT 2"));
        var reader = await batch.ExecuteReaderAsync(
            CommandBehavior.CloseConnection,
            CancellationToken.None);

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        (await reader.NextResultAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        await reader.DisposeAsync();

        reader.IsClosed.Should().BeTrue();
        connection.State.Should().Be(ConnectionState.Closed);
        await connection.DisposeAsync();
    }

    private static SqliteConnection CreateSqliteConnection()
        => new(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False",
            new AsyncOnlyManagedDatabaseFactory());

    private static global::Ahtola.AhtolaConnection CreateAhtolaConnection()
        => new(
            "Data Source=:memory:;Local Provider=Managed",
            new AsyncOnlyManagedDatabaseFactory());

    private sealed class AsyncOnlyManagedDatabaseFactory : IManagedDatabaseFactory
    {
        public string DataSource => ":memory:";

        public bool IsReadOnly => false;

        public ValueTask<IManagedDatabaseAdapter> OpenDatabaseAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IManagedDatabaseAdapter>(
                ManagedDatabaseAdapter.Open(":memory:"));
        }
    }

    private sealed class IdentityPageCodec : IPageCodec
    {
        private static readonly PageCodecId Id = new("browser-config"u8.ToArray().Concat(new byte[2]).ToArray());

        public PageCodecId CodecId => Id;

        public byte RequiredReservedBytes => 0;

        public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
            => input.CopyTo(output);

        public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
            => input.CopyTo(output);
    }
}
