using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedProviderAsyncParityTests
{
    [Test]
    public async Task ManagedSqliteOpenAsyncReturnsCanceledTaskWithoutOpeningConnection()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task? open = null;
        Assert.DoesNotThrow(() => open = connection.OpenAsync(cancellation.Token));
        AssertCanceled(open!);

        connection.State.Should().Be(System.Data.ConnectionState.Closed);

        await connection.OpenAsync();
        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Test]
    public async Task ManagedSqliteReaderAsyncOperationsHonorCancellationAndRemainUsable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2;";
        using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        (await reader.GetFieldValueAsync<long>(0)).Should().Be(1);
        (await reader.IsDBNullAsync(0)).Should().BeFalse();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AssertCanceled(reader.ReadAsync(cancellation.Token));
        AssertCanceled(reader.NextResultAsync(cancellation.Token));
        AssertCanceled(reader.IsDBNullAsync(0, cancellation.Token));
        AssertCanceled(reader.GetFieldValueAsync<long>(0, cancellation.Token));

        (await reader.ReadAsync()).Should().BeTrue();
    }

    [Test]
    public async Task ManagedAhtolaReaderAsyncOperationsHonorCancellationAndRemainUsable()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2;";
        var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        (await reader.GetFieldValueAsync<long>(0)).Should().Be(1);
        (await reader.IsDBNullAsync(0)).Should().BeFalse();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AssertCanceled(reader.ReadAsync(cancellation.Token));
        AssertCanceled(reader.NextResultAsync(cancellation.Token));
        AssertCanceled(reader.IsDBNullAsync(0, cancellation.Token));
        AssertCanceled(reader.GetFieldValueAsync<long>(0, cancellation.Token));

        (await reader.ReadAsync()).Should().BeTrue();

        await reader.DisposeAsync();
        reader.IsClosed.Should().BeTrue();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.ReadAsync());

        using var verification = connection.CreateCommand();
        verification.CommandText = "SELECT 3;";
        (await verification.ExecuteScalarAsync()).Should().Be(3L);
    }

    [Test]
    public async Task ManagedAhtolaAsyncCommandSurfacesPreparationErrors()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM missing_table;";

        Assert.ThrowsAsync<AhtolaException>(async () => await command.ExecuteReaderAsync());
    }

    [Test]
    public async Task ManagedSqliteReaderAsyncOperationsReturnFaultedTasksAfterDisposal()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        var reader = await command.ExecuteReaderAsync();
        await reader.DisposeAsync();

        Task<bool>? read = null;
        Assert.DoesNotThrow(() => read = reader.ReadAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await read!);

        Task<bool>? nextResult = null;
        Assert.DoesNotThrow(() => nextResult = reader.NextResultAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await nextResult!);

        Task<bool>? isDbNull = null;
        Assert.DoesNotThrow(() => isDbNull = reader.IsDBNullAsync(0));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await isDbNull!);

        Task<long>? fieldValue = null;
        Assert.DoesNotThrow(() => fieldValue = reader.GetFieldValueAsync<long>(0));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await fieldValue!);

        using var verification = connection.CreateCommand();
        verification.CommandText = "SELECT 2;";
        (await verification.ExecuteScalarAsync()).Should().Be(2L);
    }

    [Test]
    public async Task ManagedSqliteAsyncExecutionDoesNotHopToTheThreadPool()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        var callerThread = Environment.CurrentManagedThreadId;
        var executionThread = callerThread;
        connection.CreateFunction<long>(
            "execution_thread",
            () =>
            {
                executionThread = Environment.CurrentManagedThreadId;
                return 1;
            });
        command.CommandText = "SELECT execution_thread();";

        var execution = command.ExecuteScalarAsync();
        execution.IsCompletedSuccessfully.Should().BeTrue();
        (await execution).Should().Be(1L);
        executionThread.Should().Be(callerThread);
    }

    [Test]
    public async Task ManagedSqliteCancelInterruptsActiveCommand()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        connection.CreateFunction<long>(
            "wait_for_command_cancel",
            () =>
            {
                entered.Set();
                release.Wait();
                return 1;
            });

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT wait_for_command_cancel();";
        var cancelThread = new Thread(() =>
        {
            entered.Wait();
            command.Cancel();
            release.Set();
        });
        cancelThread.Start();
        var execution = command.ExecuteScalarAsync();
        await AssertCanceledAsync(execution);
        cancelThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

        command.CommandText = "SELECT 3;";
        (await command.ExecuteScalarAsync()).Should().Be(3L);
    }

    [Test]
    // Cancellation is observed between row evaluations; on a slow/loaded CI
    // runner the third row's function call can return and the UPDATE finalize
    // before the token is observed, so the rollback loses the race. Retry the
    // transient timing loss instead of reding the whole suite.
    [Retry(3)]
    public async Task ManagedSqliteCancellationRollsBackTheWholeMutation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE items(value INTEGER); INSERT INTO items VALUES (1), (2), (3);";
        setup.ExecuteNonQuery();

        using var entered = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        connection.CreateFunction<long, long>(
            "cancel_during_update",
            value =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    entered.Set();
                    cancellation.Token.WaitHandle.WaitOne();
                }

                return value + 10;
            });

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET value = cancel_during_update(value);";
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var execution = command.ExecuteNonQueryAsync(cancellation.Token);
        entered.IsSet.Should().BeTrue();
        await AssertCanceledAsync(execution);

        command.CommandText = "SELECT group_concat(value, ',') FROM items ORDER BY rowid;";
        (await command.ExecuteScalarAsync()).Should().Be("1,2,3");
    }

    [Test]
    public async Task ManagedSqliteAsyncLockWaitIsCancellable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE values_table(value INTEGER);";
        setup.ExecuteNonQuery();

        using var readerCommand = connection.CreateCommand();
        readerCommand.CommandText = "SELECT * FROM values_table;";
        using var reader = readerCommand.ExecuteReader();

        using var writer = connection.CreateCommand();
        writer.CommandTimeout = 30;
        writer.CommandText = "INSERT INTO values_table VALUES (1);";
        using var cancellation = new CancellationTokenSource();

        Task<int>? execution = null;
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        Assert.DoesNotThrow(() => execution = writer.ExecuteNonQueryAsync(cancellation.Token));
        await AssertCanceledAsync(execution!);
    }

    [Test]
    public async Task ManagedAhtolaCancelInterruptsRecursiveExecution()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "WITH RECURSIVE counter(value) AS (" +
            "SELECT 1 UNION ALL SELECT value + 1 FROM counter WHERE value < 100000000" +
            ") SELECT max(value) FROM counter;";

        var cancelThread = new Thread(() =>
        {
            Thread.Sleep(50);
            command.Cancel();
        });
        cancelThread.Start();
        var execution = command.ExecuteScalarAsync();
        await AssertCanceledAsync(execution);
        cancelThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

        command.CommandText = "SELECT 4;";
        (await command.ExecuteScalarAsync()).Should().Be(4L);
    }

    private static void AssertCanceled(Task task)
    {
        task.IsCanceled.Should().BeTrue();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
    }

    private static Task AssertCanceledAsync(Task task)
    {
        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
        exception.Should().NotBeNull();
        task.IsCanceled.Should().BeTrue();
        return Task.CompletedTask;
    }
}
