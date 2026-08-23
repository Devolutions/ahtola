using System.Data;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Ahtola.Core;
using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for a full-branch review of the managed schema dispatch and async reader close
/// paths:
/// 1) AhtolaSchemaCollections must only take the managed fast path when the connection is
///    actually backed by a managed adapter; native local and remote connections implement
///    IManagedSchemaConnection too, but must fall back to the provider-neutral PRAGMA path.
/// 2) SqliteDataReader.CloseCoreAsync must stay exception-safe for any failure (not just the
///    engine's own exception types), always releasing the statement/delegated reader and
///    deregistering from the connection.
/// 3) SqliteDataReader's browser declared-type shortcut must resolve against the current
///    statement (_currentSql), not the whole (possibly multi-statement) command text.
/// </summary>
public sealed class ManagedSchemaAndReaderCorrectnessTests
{
    [Test]
    public void NativeConnection_GetValue_ResolvesDeclaredType_WithoutReachingManagedConnection()
    {
        AhtolaNativeProvider.Register(new FakeNativeProviderFactory());

        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Native");
        connection.Open();
        connection.IsManaged.Should().BeFalse("a native connection has no managed adapter to dispatch schema lookups to");

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE widgets (id GUID, payload BLOB)";
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO widgets (id, payload) VALUES (X'0102030405060708090A0B0C0D0E0F10', X'AABBCCDD')";
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM widgets";
        using var reader = select.ExecuteReader();
        reader.Read().Should().BeTrue();

        // Before the fix, GetValue's declared-type lookup unconditionally preferred the managed
        // fast path (AhtolaSchemaCollections.GetTableColumns), which threw InvalidOperationException
        // ("Ahtola database is closed.") for this native (non-managed) connection.
        Action getValue = () => reader.GetValue(0);
        getValue.Should().NotThrow();
        reader.GetValue(0).Should().BeOfType<Guid>();
    }

    [Test]
    public void RemoteAhtolaConnection_GetSchemaTable_FallsBackToPragma_InsteadOfThrowing()
    {
        using var handler = new MinimalRemoteSchemaHandler();
        var priorFactory = global::Ahtola.AhtolaConnection.RemoteMessageHandlerFactory;
        global::Ahtola.AhtolaConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new global::Ahtola.AhtolaConnection(
                "Data Source=https://example.test/db;Auth Token=token");
            connection.Open();
            connection.IsRemote.Should().BeTrue();
            connection.IsManaged.Should().BeFalse("a plain remote connection has no managed adapter either");

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM widgets";
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();

            // Before the fix, BuildReaderSchemaTable's call into GetTableColumns unconditionally
            // preferred the managed fast path and threw for this remote (non-managed) connection.
            Action getSchemaTable = () => reader.GetSchemaTable();
            getSchemaTable.Should().NotThrow();
            var schema = reader.GetSchemaTable();
            schema.Should().NotBeNull();
            schema!.Rows.Count.Should().Be(1);
            schema.Rows[0][System.Data.Common.SchemaTableColumn.ColumnName].Should().Be("value");
        }
        finally
        {
            global::Ahtola.AhtolaConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public void RemoteSqliteConnection_GetSchemaTable_FallsBackToPragma_ViaDelegatedAhtolaReader()
    {
        using var handler = new MinimalRemoteSchemaHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new SqliteConnection(
                "Data Source=https://example.test/db;Auth Token=token");
            connection.Open();
            connection.IsRemoteConnection.Should().BeTrue();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM widgets";
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();

            // SqliteDataReader delegates GetSchemaTable to the inner AhtolaRemoteDataReader for
            // remote connections, so this exercises the exact same AhtolaSchemaCollections
            // dispatch fix through the Sqlite facade.
            Action getSchemaTable = () => reader.GetSchemaTable();
            getSchemaTable.Should().NotThrow();
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public async Task BrowserReaderDisposalFailure_StillClosesReader_AndConnectionRecovers()
    {
        var factory = new FaultInjectingManagedDatabaseFactory();
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False",
            factory);
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (value INTEGER)";
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t VALUES (1)";
            await insert.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM t";
        var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        factory.PendingStatementDisposeFault = new SimulatedBrowserDisposalException(
            "Simulated OPFS statement disposal failure.");

        // CloseAsync (throwOnError: true) must still surface the injected, non-engine failure...
        Func<Task> close = async () => await reader.CloseAsync();
        await close.Should().ThrowAsync<SimulatedBrowserDisposalException>();

        // ...but cleanup must have happened regardless: the reader is closed and deregistered.
        reader.IsClosed.Should().BeTrue();
        connection.HasOpenReader.Should().BeFalse();

        // The connection recovers and can run further commands after the failed close.
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM t";
        (await verify.ExecuteScalarAsync()).Should().Be(1L);

        await connection.CloseAsync();
        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Test]
    public async Task ThrowingCloseCallback_StillDeregistersReader_AndConnectionRecovers()
    {
        // FinishCloseAsync used to call _closeCallback() (command/batch bookkeeping) before
        // ReaderClosed/_isClosed, with nothing to catch a callback failure: if it threw, the
        // reader was never deregistered or marked closed. Constructing the reader directly (with
        // a throwing callback) reaches FinishCloseAsync without needing a real command-level
        // failure to trigger it.
        await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        var thrown = new InvalidOperationException("Simulated close-callback failure.");
        var reader = new SqliteDataReader(
            command,
            recordsAffected: 0,
            CommandBehavior.Default,
            closeCallback: () => throw thrown);

        Func<Task> close = async () => await reader.CloseAsync();
        (await close.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(thrown);

        reader.IsClosed.Should().BeTrue();
        connection.HasOpenReader.Should().BeFalse();

        // The connection recovers and can still run further commands after the failed close.
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT 1";
        (await verify.ExecuteScalarAsync()).Should().Be(1L);

        await connection.CloseAsync();
        connection.State.Should().Be(ConnectionState.Closed);
    }

    [Test]
    public void ThrowingCloseCallback_SynchronousCloseStillDeregistersReader()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        var thrown = new InvalidOperationException("Simulated synchronous close-callback failure.");
        var reader = new SqliteDataReader(
            command,
            recordsAffected: 0,
            CommandBehavior.Default,
            closeCallback: () => throw thrown);

        Action close = reader.Close;
        close.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(thrown);
        reader.IsClosed.Should().BeTrue();
        connection.HasOpenReader.Should().BeFalse();

        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT 1";
        verify.ExecuteScalar().Should().Be(1L);
    }

    [Test]
    public async Task ConnectionOwnedAsyncCloseAggregatesDisposalAndCallbackFailures()
    {
        var factory = new FaultInjectingManagedDatabaseFactory();
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False",
            factory);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var statement = SqliteStatementAdapter.FromManaged(
            await connection.ManagedConnection.PrepareAsync(command.CommandText));
        var callbackFailure = new InvalidOperationException("Simulated async close-callback failure.");
        var reader = new SqliteDataReader(
            command,
            statement,
            command.CommandText,
            [],
            recordsAffected: 0,
            CommandBehavior.Default,
            closeCallback: () => throw callbackFailure);
        var disposalFailure = new SimulatedBrowserDisposalException(
            "Simulated async connection-owned disposal failure.");
        factory.PendingStatementDisposeFault = disposalFailure;

        var aggregate = Assert.ThrowsAsync<AggregateException>(
            async () => await ((IConnectionOwnedReader)reader).CloseFromConnectionAsync());

        aggregate!.Flatten().InnerExceptions.Should().Contain(disposalFailure).And.Contain(callbackFailure);
        reader.IsClosed.Should().BeTrue();
        connection.HasOpenReader.Should().BeFalse();
    }

    [Test]
    public void SynchronousCloseAggregatesDisposalAndCallbackFailures()
    {
        var factory = new FaultInjectingManagedDatabaseFactory();
        var database = new FaultInjectingDatabaseAdapter(
            ManagedDatabaseAdapter.Open(":memory:"),
            factory);
        using var connection = new SqliteConnection(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var statement = SqliteStatementAdapter.FromManaged(
            connection.ManagedConnection.Prepare(command.CommandText));
        var callbackFailure = new InvalidOperationException("Simulated sync close-callback failure.");
        var reader = new SqliteDataReader(
            command,
            statement,
            command.CommandText,
            [],
            recordsAffected: 0,
            CommandBehavior.Default,
            closeCallback: () => throw callbackFailure);
        var disposalFailure = new SimulatedBrowserDisposalException(
            "Simulated synchronous statement disposal failure.");
        factory.PendingStatementSyncDisposeFault = disposalFailure;

        var aggregate = Assert.Throws<AggregateException>(reader.Close);

        aggregate!.Flatten().InnerExceptions.Should().Contain(disposalFailure).And.Contain(callbackFailure);
        reader.IsClosed.Should().BeTrue();
        connection.HasOpenReader.Should().BeFalse();
    }

    [Test]
    public async Task BrowserReaderDisposalFailure_DisposeAsyncSwallowsOperationFailure_ButStillCleansUp()
    {
        var factory = new FaultInjectingManagedDatabaseFactory();
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False",
            factory);
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (value INTEGER)";
            await create.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM t";
        var reader = await command.ExecuteReaderAsync();

        factory.PendingStatementDisposeFault = new SimulatedBrowserDisposalException(
            "Simulated OPFS statement disposal failure.");

        // DisposeAsync (throwOnError: false) tolerates the operation failure, matching prior
        // behavior for the engine's own exception types, but must still deregister the reader.
        Func<Task> dispose = async () => await reader.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        reader.IsClosed.Should().BeTrue();
        connection.HasOpenReader.Should().BeFalse();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM t";
        (await verify.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Test]
    public async Task MultiResultSelect_SecondBlobRemainsBlob_DespiteFirstGuidDeclaredType()
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Local Provider=Managed;Pooling=False",
            new AsyncOnlyManagedDatabaseFactory());
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ids (id GUID); CREATE TABLE blobs (payload BLOB);";
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO ids VALUES (X'0102030405060708090A0B0C0D0E0F10'); "
                + "INSERT INTO blobs VALUES (X'AABBCCDD');";
            await insert.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM ids; SELECT payload FROM blobs;";
        var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetValue(0).Should().BeOfType<Guid>();

        (await reader.NextResultAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();

        // Before the fix, the browser metadata shortcut re-parsed _command.CommandText (the
        // whole batch), which always anchored on the FIRST statement ("SELECT id FROM ids"), so
        // this second result set's "payload" BLOB column inherited the "id" column's GUID
        // declared type and was incorrectly converted (or failed to convert) to a Guid.
        Action getValue = () => reader.GetValue(0);
        getValue.Should().NotThrow();
        reader.GetValue(0).Should().BeOfType<byte[]>();
        ((byte[])reader.GetValue(0)).Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);

        await reader.DisposeAsync();
    }

    private sealed class SimulatedBrowserDisposalException(string message) : Exception(message);

    #region Fake native provider (non-managed connection with real SQL execution)

    private sealed class FakeNativeProviderFactory : AhtolaNativeProviderFactory
    {
        public override AhtolaNativeDatabase OpenDatabase(
            string path,
            AhtolaEncryptionCipher? cipher,
            string? encryptionKey)
            => new FakeNativeDatabase(path);
    }

    private sealed class FakeNativeDatabase : AhtolaNativeDatabase
    {
        private readonly ManagedDatabaseAdapter _database;
        private readonly IManagedConnectionAdapter _connection;
        private bool _disposed;

        public FakeNativeDatabase(string path)
        {
            _database = ManagedDatabaseAdapter.Open(path);
            _connection = _database.Connect();
        }

        public override bool IsInvalid => _disposed;

        public override AhtolaNativeStatement PrepareStatement(string sql)
            => new FakeNativeStatement(_connection.Prepare(sql));

        public override void SetBusyTimeout(TimeSpan timeout) => _connection.BusyTimeout = timeout;

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _database.Dispose();
        }
    }

    private sealed class FakeNativeStatement(IManagedStatementAdapter statement) : AhtolaNativeStatement
    {
        private bool _disposed;

        public override bool IsInvalid => _disposed;

        public override int ParameterCount => statement.ParameterCount;

        public override void BindParameter(int index, AhtolaValue value)
            => statement.Bind(index - 1, ToSqlValue(value));

        public override int BindNamedParameter(string name, AhtolaValue value)
        {
            var index = statement.GetParameterIndex(name);
            statement.Bind(index, ToSqlValue(value));
            return index + 1;
        }

        public override string? GetParameterName(int index) => statement.GetParameterName(index - 1);

        public override bool Read() => statement.Step() == StatementStepResult.Row;

        public override void Interrupt()
        {
        }

        public override AhtolaValue GetValue(int ordinal) => ToAhtolaValue(statement.GetValue(ordinal));

        public override string GetName(int ordinal) => statement.GetColumnName(ordinal);

        public override int FieldCount => statement.GetColumnCount();

        public override int RowsAffected => statement.RowsAffected;

        public override bool HasRows => statement.HasRows();

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            statement.Dispose();
        }

        private static SqlValue ToSqlValue(AhtolaValue value) => value.ValueType switch
        {
            AhtolaValueType.Empty or AhtolaValueType.Null => SqlValue.Null,
            AhtolaValueType.Integer => SqlValue.Integer(value.IntValue),
            AhtolaValueType.Real => SqlValue.Real(value.RealValue),
            AhtolaValueType.Text => SqlValue.Text(value.StringValue),
            AhtolaValueType.Blob => SqlValue.Blob(value.BlobValue),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

        private static AhtolaValue ToAhtolaValue(SqlValue value) => value.Kind switch
        {
            SqlValueKind.Null => AhtolaValue.Null(),
            SqlValueKind.Integer => AhtolaValue.Int(value.AsInteger()),
            SqlValueKind.Real => AhtolaValue.Real(value.AsReal()),
            SqlValueKind.Text => AhtolaValue.String(value.AsText()),
            SqlValueKind.Blob => AhtolaValue.Blob(value.AsBlob().ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    #endregion

    #region Fake remote (Hrana) handler

    /// <summary>
    /// Responds to any "SELECT ..." statement with a single "value"/INTEGER 42 row (enough for
    /// TryGetSelectSource to resolve a FROM-clause table), and to anything else (in particular
    /// the PRAGMA table_info fallback) with an empty, successful result.
    /// </summary>
    private sealed class MinimalRemoteSchemaHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var cursor = request.RequestUri!.AbsolutePath.EndsWith("/v3/cursor", StringComparison.Ordinal);
            var root = document.RootElement;
            if (!cursor
                && root.GetProperty("requests")[0].GetProperty("type").GetString() == "close")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"baton":null,"results":[{"type":"ok","response":{"type":"close"}}]}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            var statement = cursor
                ? root.GetProperty("batch").GetProperty("steps")[0].GetProperty("stmt")
                : root.GetProperty("requests")[0].GetProperty("stmt");
            var sql = statement.GetProperty("sql").GetString();
            if (cursor)
            {
                var cursorResponse = sql!.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    ? """
                      {"baton":"schema-cursor","base_url":null}
                      {"type":"step_begin","step":0,"cols":[{"name":"value","decltype":"INTEGER"}]}
                      {"type":"row","row":[{"type":"integer","value":"42"}]}
                      {"type":"step_end","affected_row_count":0,"last_insert_rowid":null}
                      {"type":"replication_index","replication_index":null}
                      """
                    : """
                      {"baton":"schema-cursor","base_url":null}
                      {"type":"step_begin","step":0,"cols":[]}
                      {"type":"step_end","affected_row_count":0,"last_insert_rowid":null}
                      {"type":"replication_index","replication_index":null}
                      """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(cursorResponse + "\n", Encoding.UTF8, "application/x-ndjson"),
                };
            }

            var response = sql!.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                ? """{"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"value","decltype":"INTEGER"}],"rows":[[{"type":"integer","value":"42"}]],"affected_row_count":0}}}]}"""
                : """{"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[],"rows":[],"affected_row_count":0}}}]}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    #endregion

    #region Browser-mode (async-only) managed database factories

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

    /// <summary>
    /// An async-only (browser-shaped) managed database factory whose statements can be armed to
    /// throw an arbitrary, non-engine exception the next time one is disposed asynchronously -
    /// simulating an OPFS/Web Crypto failure surfacing through statement teardown.
    /// </summary>
    private sealed class FaultInjectingManagedDatabaseFactory : IManagedDatabaseFactory
    {
        public string DataSource => ":memory:";

        public bool IsReadOnly => false;

        public Exception? PendingStatementDisposeFault { get; set; }

        public Exception? PendingStatementSyncDisposeFault { get; set; }

        public ValueTask<IManagedDatabaseAdapter> OpenDatabaseAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IManagedDatabaseAdapter>(
                new FaultInjectingDatabaseAdapter(ManagedDatabaseAdapter.Open(DataSource), this));
        }
    }

    private sealed class FaultInjectingDatabaseAdapter : IManagedDatabaseAdapter
    {
        private readonly ManagedDatabaseAdapter _inner;
        private readonly FaultInjectingConnectionAdapter _connection;

        public FaultInjectingDatabaseAdapter(ManagedDatabaseAdapter inner, FaultInjectingManagedDatabaseFactory faults)
        {
            _inner = inner;
            _connection = new FaultInjectingConnectionAdapter(inner.Connect(), faults);
        }

        public IManagedConnectionAdapter Connect() => _connection;

        public IManagedConnectionAdapter Connection => _connection;

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class FaultInjectingConnectionAdapter(
        IManagedConnectionAdapter inner,
        FaultInjectingManagedDatabaseFactory faults) : IManagedConnectionAdapter
    {
        public bool HasAttachedDatabases => inner.HasAttachedDatabases;

        public TimeSpan BusyTimeout
        {
            get => inner.BusyTimeout;
            set => inner.BusyTimeout = value;
        }

        public ManagedConnectionHooks Hooks => inner.Hooks;

        public IManagedStatementAdapter Prepare(string sql)
            => new FaultInjectingStatementAdapter(inner.Prepare(sql), faults);

        public async ValueTask<IManagedStatementAdapter> PrepareAsync(
            string sql,
            CancellationToken cancellationToken = default)
            => new FaultInjectingStatementAdapter(
                await inner.PrepareAsync(sql, cancellationToken).ConfigureAwait(false),
                faults);

        public void ResetForPooling() => inner.ResetForPooling();

        public void RegisterScalarFunction(string name, int arity, Func<IReadOnlyList<SqlValue>, SqlValue> function)
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

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FaultInjectingStatementAdapter(
        IManagedStatementAdapter inner,
        FaultInjectingManagedDatabaseFactory faults) : IManagedStatementAdapter
    {
        public int ParameterCount => inner.ParameterCount;

        public int RowsAffected => inner.RowsAffected;

        public void Bind(int index, SqlValue value) => inner.Bind(index, value);

        public int GetParameterIndex(string name) => inner.GetParameterIndex(name);

        public StatementStepResult Step() => inner.Step();

        public ValueTask<StatementStepResult> StepAsync(CancellationToken cancellationToken = default)
            => inner.StepAsync(cancellationToken);

        public bool HasRows() => inner.HasRows();

        public void Reset() => inner.Reset();

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
            => inner.ResetAsync(cancellationToken);

        public void ClearBindings() => inner.ClearBindings();

        public SqlValue GetValue(int ordinal) => inner.GetValue(ordinal);

        public string GetColumnName(int ordinal) => inner.GetColumnName(ordinal);

        public int GetColumnCount() => inner.GetColumnCount();

        public string? GetParameterName(int index) => inner.GetParameterName(index);

        public void Dispose()
        {
            var fault = faults.PendingStatementSyncDisposeFault;
            faults.PendingStatementSyncDisposeFault = null;
            inner.Dispose();
            if (fault is not null)
                throw fault;
        }

        public ValueTask DisposeAsync()
        {
            var fault = faults.PendingStatementDisposeFault;
            faults.PendingStatementDisposeFault = null;

            // The real statement is still released even when a fault is armed: the synthetic
            // failure models a disposal channel (e.g. a JS interop promise) that reports an
            // error after the underlying resource has, in fact, already been freed.
            inner.Dispose();

            if (fault is not null)
                throw fault;

            return ValueTask.CompletedTask;
        }
    }

    #endregion
}
