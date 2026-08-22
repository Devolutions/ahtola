using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedProviderTrueAsyncTests
{
    [Test]
    public async Task ManagedAhtolaAsyncExecutionAwaitsAdapterPrepareAndStep()
    {
        await using var adapter = new GatedDatabaseAdapter();
        using var connection = new AhtolaConnection(adapter);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 7;";

        var execution = command.ExecuteReaderAsync();
        adapter.PrepareStarted.Task.IsCompleted.Should().BeTrue();
        execution.IsCompleted.Should().BeFalse();

        adapter.ReleasePrepare();
        await using var reader = await execution;
        var read = reader.ReadAsync();
        adapter.StepStarted.Task.IsCompleted.Should().BeTrue();
        read.IsCompleted.Should().BeFalse();

        adapter.ReleaseStep();
        (await read).Should().BeTrue();
        reader.GetInt64(0).Should().Be(7);
    }

    [Test]
    public async Task ManagedSqliteAsyncExecutionAwaitsAdapterPrepareAndStep()
    {
        await using var adapter = new GatedDatabaseAdapter();
        using var connection = new SqliteConnection(adapter);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 11;";

        var execution = command.ExecuteReaderAsync();
        adapter.PrepareStarted.Task.IsCompleted.Should().BeTrue();
        execution.IsCompleted.Should().BeFalse();

        adapter.ReleasePrepare();
        await using var reader = await execution;
        var read = reader.ReadAsync();
        adapter.StepStarted.Task.IsCompleted.Should().BeTrue();
        read.IsCompleted.Should().BeFalse();

        adapter.ReleaseStep();
        (await read).Should().BeTrue();
        reader.GetInt64(0).Should().Be(11);
    }

    [Test]
    public async Task ManagedLocalAsyncExecutionCompletesInlineWithoutThreadPoolHop()
    {
        await using var adapter = new GatedDatabaseAdapter(released: true);
        using var connection = new AhtolaConnection(adapter);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 13;";
        var callerThread = Environment.CurrentManagedThreadId;

        var execution = command.ExecuteReaderAsync();
        execution.IsCompletedSuccessfully.Should().BeTrue();
        await using var reader = await execution;
        var read = reader.ReadAsync();
        read.IsCompletedSuccessfully.Should().BeTrue();
        (await read).Should().BeTrue();

        adapter.PrepareThreadId.Should().Be(callerThread);
        adapter.StepThreadId.Should().Be(callerThread);
    }

    [Test]
    public async Task ManagedLocalAsyncCancellationPreservesTheExactToken()
    {
        await using var adapter = new GatedDatabaseAdapter(prepareReleased: true);
        using var connection = new AhtolaConnection(adapter);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 17;";
        await using var reader = await command.ExecuteReaderAsync();
        using var cancellation = new CancellationTokenSource();

        var read = reader.ReadAsync(cancellation.Token);
        read.IsCompleted.Should().BeFalse();
        cancellation.Cancel();

        var exception = Assert.CatchAsync<OperationCanceledException>(async () => await read);
        exception.Should().NotBeNull();
        exception!.CancellationToken.Should().Be(cancellation.Token);
        read.IsCanceled.Should().BeTrue();
    }

    [Test]
    public async Task ManagedLocalAsyncStepErrorsKeepProviderMapping()
    {
        await using var ahtolaAdapter = new GatedDatabaseAdapter(
            released: true,
            stepError: new EmbeddedSqlException("async-step-failure"));
        using var ahtolaConnection = new AhtolaConnection(ahtolaAdapter);
        using var ahtolaCommand = ahtolaConnection.CreateCommand();
        ahtolaCommand.CommandText = "SELECT 19;";
        await using var ahtolaReader = await ahtolaCommand.ExecuteReaderAsync();

        var ahtolaError = Assert.CatchAsync<AhtolaException>(async () => await ahtolaReader.ReadAsync());
        ahtolaError.Should().NotBeNull();
        ahtolaError!.Message.Should().Contain("async-step-failure");

        await using var sqliteAdapter = new GatedDatabaseAdapter(
            released: true,
            stepError: new EmbeddedSqlException("async-step-failure"));
        using var sqliteConnection = new SqliteConnection(sqliteAdapter);
        using var sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT 23;";
        await using var sqliteReader = await sqliteCommand.ExecuteReaderAsync();

        var sqliteError = Assert.CatchAsync<SqliteException>(async () => await sqliteReader.ReadAsync());
        sqliteError.Should().NotBeNull();
        sqliteError!.SqliteErrorCode.Should().Be(1);
        sqliteError.Message.Should().Contain("async-step-failure");
    }

    [Test]
    public async Task ManagedLocalSynchronousExecutionStillUsesSynchronousAdapterMethods()
    {
        await using var adapter = new GatedDatabaseAdapter();
        using var ahtolaConnection = new AhtolaConnection(adapter);
        using var command = ahtolaConnection.CreateCommand();
        command.CommandText = "SELECT 29;";

        command.ExecuteScalar().Should().Be(29L);
        adapter.SynchronousPrepareCount.Should().Be(1);
        adapter.SynchronousStepCount.Should().BeGreaterThan(0);
        adapter.PrepareStarted.Task.IsCompleted.Should().BeFalse();
        adapter.StepStarted.Task.IsCompleted.Should().BeFalse();
    }

    private sealed class GatedDatabaseAdapter : IManagedDatabaseAdapter
    {
        private readonly ManagedDatabaseAdapter _inner = ManagedDatabaseAdapter.Open(":memory:");
        private readonly GatedConnectionAdapter _connection;

        public GatedDatabaseAdapter(
            bool released = false,
            bool prepareReleased = false,
            Exception? stepError = null)
        {
            _connection = new GatedConnectionAdapter(
                _inner.Connect(),
                prepareReleased || released,
                released,
                stepError);
        }

        public TaskCompletionSource<bool> PrepareStarted => _connection.PrepareStarted;

        public TaskCompletionSource<bool> StepStarted => _connection.StepStarted;

        public int PrepareThreadId => _connection.PrepareThreadId;

        public int StepThreadId => _connection.StepThreadId;

        public int SynchronousPrepareCount => _connection.SynchronousPrepareCount;

        public int SynchronousStepCount => _connection.SynchronousStepCount;

        public IManagedConnectionAdapter Connect() => _connection;

        public IManagedConnectionAdapter Connection => _connection;

        public void ReleasePrepare() => _connection.ReleasePrepare();

        public void ReleaseStep() => _connection.ReleaseStep();

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class GatedConnectionAdapter(
        IManagedConnectionAdapter inner,
        bool prepareReleased,
        bool stepReleased,
        Exception? stepError) : IManagedConnectionAdapter
    {
        private readonly TaskCompletionSource<bool> _prepareRelease = CreateGate(prepareReleased);
        private readonly TaskCompletionSource<bool> _stepRelease = CreateGate(stepReleased);

        public TaskCompletionSource<bool> PrepareStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> StepStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PrepareThreadId { get; private set; }

        public int StepThreadId { get; private set; }

        public int SynchronousPrepareCount { get; private set; }

        public int SynchronousStepCount { get; private set; }

        public bool HasAttachedDatabases => inner.HasAttachedDatabases;

        public TimeSpan BusyTimeout
        {
            get => inner.BusyTimeout;
            set => inner.BusyTimeout = value;
        }

        public ManagedConnectionHooks Hooks => inner.Hooks;

        public IManagedStatementAdapter Prepare(string sql)
        {
            SynchronousPrepareCount++;
            return new GatedStatementAdapter(inner.Prepare(sql), this, stepError);
        }

        public async ValueTask<IManagedStatementAdapter> PrepareAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            PrepareThreadId = Environment.CurrentManagedThreadId;
            PrepareStarted.TrySetResult(true);
            await _prepareRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var statement = await inner.PrepareAsync(sql, cancellationToken).ConfigureAwait(false);
            return new GatedStatementAdapter(statement, this, stepError);
        }

        public void ReleasePrepare() => _prepareRelease.TrySetResult(true);

        public void ReleaseStep() => _stepRelease.TrySetResult(true);

        public void ResetForPooling() => inner.ResetForPooling();

        public IManagedIncrementalBlobAdapter OpenBlob(
            string databaseName,
            string tableName,
            string columnName,
            long rowId,
            bool readOnly = false)
            => inner.OpenBlob(databaseName, tableName, columnName, rowId, readOnly);

        public void RegisterScalarFunction(
            string name,
            int arity,
            Func<IReadOnlyList<SqlValue>, SqlValue> function)
            => inner.RegisterScalarFunction(name, arity, function);

        public int UnregisterScalarFunctions(string name) => inner.UnregisterScalarFunctions(name);

        public void RegisterAggregateFunction(
            string name,
            int arity,
            SqlValue seed,
            Func<SqlValue, IReadOnlyList<SqlValue>, SqlValue> step,
            Func<SqlValue, SqlValue> finalize)
            => inner.RegisterAggregateFunction(name, arity, seed, step, finalize);

        public int UnregisterAggregateFunctions(string name) => inner.UnregisterAggregateFunctions(name);

        public void RegisterCollation(string name, Func<string, string, int> compare)
            => inner.RegisterCollation(name, compare);

        public bool UnregisterCollation(string name) => inner.UnregisterCollation(name);

        public void CopySnapshotTo(IManagedConnectionAdapter destination)
            => inner.CopySnapshotTo(destination);

        public void CopySnapshotTo(
            IManagedConnectionAdapter destination,
            string destinationName,
            string sourceName)
            => inner.CopySnapshotTo(destination, destinationName, sourceName);

        public void ApplySnapshotPragmaHeader(int schemaVersion, int userVersion, int applicationId)
            => inner.ApplySnapshotPragmaHeader(schemaVersion, userVersion, applicationId);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TaskCompletionSource<bool> CreateGate(bool released)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (released)
                gate.SetResult(true);
            return gate;
        }

        private sealed class GatedStatementAdapter(
            IManagedStatementAdapter statement,
            GatedConnectionAdapter owner,
            Exception? stepError) : IManagedStatementAdapter
        {
            public int ParameterCount => statement.ParameterCount;

            public int RowsAffected => statement.RowsAffected;

            public void Bind(int index, SqlValue value) => statement.Bind(index, value);

            public int GetParameterIndex(string name) => statement.GetParameterIndex(name);

            public StatementStepResult Step()
            {
                owner.SynchronousStepCount++;
                return statement.Step();
            }

            public StatementStepResult Step(CancellationToken cancellationToken)
            {
                owner.SynchronousStepCount++;
                return statement.Step(cancellationToken);
            }

            public async ValueTask<StatementStepResult> StepAsync(
                CancellationToken cancellationToken = default)
            {
                owner.StepThreadId = Environment.CurrentManagedThreadId;
                owner.StepStarted.TrySetResult(true);
                await owner._stepRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (stepError is not null)
                    throw stepError;
                return await statement.StepAsync(cancellationToken).ConfigureAwait(false);
            }

            public bool HasRows() => statement.HasRows();

            public void Reset() => statement.Reset();

            public ValueTask ResetAsync(CancellationToken cancellationToken = default)
                => statement.ResetAsync(cancellationToken);

            public void ClearBindings() => statement.ClearBindings();

            public SqlValue GetValue(int ordinal) => statement.GetValue(ordinal);

            public string GetColumnName(int ordinal) => statement.GetColumnName(ordinal);

            public int GetColumnCount() => statement.GetColumnCount();

            public ManagedResultValue GetResultValue(int ordinal) => statement.GetResultValue(ordinal);

            public ManagedResultColumn GetResultColumn(int ordinal) => statement.GetResultColumn(ordinal);

            public int GetResultColumnCount() => statement.GetResultColumnCount();

            public string? GetParameterName(int index) => statement.GetParameterName(index);

            public void Dispose() => statement.Dispose();

            public ValueTask DisposeAsync() => statement.DisposeAsync();
        }
    }
}
