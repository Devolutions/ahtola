using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedAsyncBlobAndBackupTests
{
    [Test]
    public async Task IncrementalBlobAdapterAsyncDefaultsPreserveSyncParityAndCancellation()
    {
        IManagedIncrementalBlobAdapter adapter = new InlineBlobAdapter([1, 2, 3]);
        var buffer = new byte[2];

        (await adapter.ReadAsync(1, buffer)).Should().Be(2);
        buffer.Should().Equal(2, 3);
        await adapter.WriteAsync(0, new byte[] { 9, 8 });

        var updated = new byte[3];
        adapter.Read(0, updated).Should().Be(3);
        updated.Should().Equal(9, 8, 3);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(
            async () => await adapter.ReadAsync(0, buffer, cancellation.Token));
        Assert.CatchAsync<OperationCanceledException>(
            async () => await adapter.WriteAsync(0, buffer, cancellation.Token));

        await adapter.DisposeAsync();
        ((InlineBlobAdapter)adapter).IsDisposed.Should().BeTrue();
    }

    [Test]
    public async Task SqliteBlobAsyncReadWriteTracksPositionAndPersistsBytes()
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'01020304');");

        var blob = await connection.OpenBlobAsync("data", "value", 1);
        var read = new byte[2];
        (await blob.ReadAsync(read)).Should().Be(2);
        read.Should().Equal(1, 2);
        blob.Position.Should().Be(2);

        await blob.WriteAsync(new byte[] { 9, 8 });
        blob.Position.Should().Be(4);
        await blob.FlushAsync();
        await blob.DisposeAsync();

        blob.CanRead.Should().BeFalse();
        Assert.Throws<ObjectDisposedException>(() => _ = blob.Length);
        (await ScalarAsync<byte[]>(connection, "SELECT value FROM data WHERE rowid = 1;"))
            .Should().Equal(1, 2, 9, 8);
    }

    [Test]
    public async Task SqliteBlobAsyncCancellationLeavesPositionAndValueUnchanged()
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            "CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'0102');");
        await using var blob = await connection.OpenBlobAsync("data", "value", 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(
            async () => await blob.ReadExactlyAsync(new byte[1], cancellation.Token));
        blob.Position.Should().Be(0);
        Assert.CatchAsync<OperationCanceledException>(
            async () => await blob.WriteAsync(new byte[] { 9 }, cancellation.Token));
        blob.Position.Should().Be(0);
        (await ScalarAsync<byte[]>(connection, "SELECT value FROM data WHERE rowid = 1;"))
            .Should().Equal(1, 2);
    }

    [Test]
    public async Task BackupDatabaseAsyncAutoOpensDestinationAndPreservesDataAndHeader()
    {
        await using var source = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False");
        await using var destination = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False");
        await source.OpenAsync();
        await ExecuteAsync(
            source,
            "CREATE TABLE data(id INTEGER PRIMARY KEY, value BLOB);"
            + "INSERT INTO data VALUES (7, X'0001FEFF');"
            + "PRAGMA user_version = 123;"
            + "PRAGMA application_id = 456;");

        await source.BackupDatabaseAsync(destination);

        destination.State.Should().Be(System.Data.ConnectionState.Open);
        (await ScalarAsync<byte[]>(destination, "SELECT value FROM data WHERE id = 7;"))
            .Should().Equal(0, 1, 254, 255);
        (await ScalarAsync<long>(destination, "PRAGMA user_version;")).Should().Be(123);
        (await ScalarAsync<long>(destination, "PRAGMA application_id;")).Should().Be(456);
    }

    [Test]
    public async Task ManagedSnapshotAsyncDefaultsHonorCancellationBeforeMutation()
    {
        using var sourceDatabase = ManagedDatabaseAdapter.Open(":memory:");
        using var destinationDatabase = ManagedDatabaseAdapter.Open(":memory:");
        var source = sourceDatabase.Connect();
        var destination = destinationDatabase.Connect();
        Execute(source, "CREATE TABLE data(value TEXT);");
        Execute(source, "INSERT INTO data VALUES ('source');");
        Execute(destination, "PRAGMA user_version = 8;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(
            async () => await source.CopySnapshotToAsync(destination, cancellation.Token));
        Assert.CatchAsync<OperationCanceledException>(
            async () => await destination.ApplySnapshotPragmaHeaderAsync(1, 2, 3, cancellation.Token));

        Scalar(destination, "PRAGMA user_version;").Should().Be(8);
        Scalar(destination, "SELECT count(*) FROM sqlite_master WHERE name = 'data';").Should().Be(0);
    }

    [Test]
    public async Task ManagedSnapshotAsyncSynchronizesDurableDestinationBeforeReturning()
    {
        var copied = false;
        IManagedConnectionAdapter source = new SnapshotSourceAdapter(() => copied = true);
        var destination = new DurableDestinationAdapter(() => copied.Should().BeTrue());

        await source.CopySnapshotToAsync(destination);

        destination.SynchronizationCount.Should().Be(1);
    }

    [Test]
    public void ManagedSnapshotRejectsConnectionDecoratorCycles()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        using var source = database.Connect();
        var first = new DecoratorAdapter();
        var second = new DecoratorAdapter();
        first.Inner = second;
        second.Inner = first;

        source.Invoking(adapter => adapter.CopySnapshotTo(first))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot form a cycle*");
    }

    [Test]
    public async Task BrowserStyleConnectionsGuardSyncBlobAndBackupSurfaces()
    {
        await using var source = CreateBrowserStyleConnection();
        await using var destination = CreateBrowserStyleConnection();
        await source.OpenAsync();
        await destination.OpenAsync();
        await ExecuteAsync(
            source,
            "CREATE TABLE data(value BLOB); INSERT INTO data(rowid, value) VALUES (1, X'0102');");

        source.Invoking(connection => connection.OpenBlob("data", "value", 1))
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*OpenBlobAsync*");
        source.Invoking(connection => new SqliteBlob(connection, "data", "value", 1))
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*OpenBlobAsync*");
        source.Invoking(connection => connection.BackupDatabase(destination))
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*BackupDatabaseAsync*");

        await using var blob = await source.OpenBlobAsync("data", "value", 1);
        blob.Invoking(value => value.Read(new byte[1], 0, 1))
            .Should().Throw<PlatformNotSupportedException>();
        blob.Invoking(value => value.Write([], 0, 0))
            .Should().Throw<PlatformNotSupportedException>();
        blob.Invoking(value => value.Flush())
            .Should().Throw<PlatformNotSupportedException>();

        blob.Position = 0;
        var bytes = new byte[2];
        (await blob.ReadAsync(bytes, 0, bytes.Length)).Should().Be(2);
        bytes.Should().Equal(1, 2);
        blob.Position = 0;
        await blob.WriteAsync(new byte[] { 3, 4 }, 0, 2);
        blob.Invoking(value => value.Dispose())
            .Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*asynchronous Stream API*");
        blob.CanRead.Should().BeTrue();
        await blob.DisposeAsync();
        blob.CanRead.Should().BeFalse();

        await source.BackupDatabaseAsync(destination);
        (await ScalarAsync<byte[]>(destination, "SELECT value FROM data WHERE rowid = 1;"))
            .Should().Equal(3, 4);
    }

    private static SqliteConnection CreateBrowserStyleConnection()
        => new(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False",
            new AsyncOnlyManagedDatabaseFactory());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scalar query returned null."));
    }

    private static void Execute(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static long Scalar(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private sealed class InlineBlobAdapter(byte[] value) : IManagedIncrementalBlobAdapter
    {
        private readonly byte[] _value = value;

        public bool IsDisposed { get; private set; }

        public long Length => _value.Length;

        public int Read(long offset, Span<byte> destination)
        {
            var count = Math.Min(destination.Length, _value.Length - checked((int)offset));
            _value.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }

        public void Write(long offset, ReadOnlySpan<byte> source)
            => source.CopyTo(_value.AsSpan(checked((int)offset)));

        public void Dispose() => IsDisposed = true;
    }

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

    private abstract class ConnectionAdapterStub : IManagedConnectionAdapter
    {
        public IManagedStatementAdapter Prepare(string sql) => throw new NotSupportedException();

        public virtual void CopySnapshotTo(IManagedConnectionAdapter destination)
            => throw new NotSupportedException();

        public void RegisterScalarFunction(
            string name,
            int arity,
            Func<IReadOnlyList<SqlValue>, SqlValue> function)
            => throw new NotSupportedException();

        public int UnregisterScalarFunctions(string name) => throw new NotSupportedException();

        public void RegisterAggregateFunction(
            string name,
            int arity,
            SqlValue seed,
            Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
            Func<SqlValue, SqlValue> finalize)
            => throw new NotSupportedException();

        public int UnregisterAggregateFunctions(string name) => throw new NotSupportedException();

        public void RegisterCollation(string name, Func<string, string, int> compare)
            => throw new NotSupportedException();

        public bool UnregisterCollation(string name) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class SnapshotSourceAdapter(Action copy) : ConnectionAdapterStub
    {
        public override void CopySnapshotTo(IManagedConnectionAdapter destination) => copy();
    }

    private sealed class DurableDestinationAdapter(Action synchronized) :
        ConnectionAdapterStub,
        IManagedConnectionDurabilityBoundary
    {
        public int SynchronizationCount { get; private set; }

        public ValueTask SynchronizeAsync()
        {
            synchronized();
            SynchronizationCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DecoratorAdapter :
        ConnectionAdapterStub,
        IManagedConnectionAdapterDecorator
    {
        public IManagedConnectionAdapter Inner { get; set; } = null!;

        public IManagedConnectionAdapter InnerConnectionAdapter => Inner;
    }
}
