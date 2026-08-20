using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ahtola.Core;
using Ahtola.Data.Sqlite;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class SqliteRemoteFacadeTests
{
    [Test]
    public void Builder_NormalizesRemoteAliases_AndProjectsAhtolaOptions()
    {
        var builder = new SqliteConnectionStringBuilder(
            "DataSource=https://example.test/db;AuthToken=secret;ReplicaPath=replica.db;"
            + "ReadYourWrites=false;SyncInterval=15;TLS=true;DefaultTimeout=7;"
            + "EncryptionCipher=aes256gcm;EncryptionKey=abcd;LocalProvider=Managed");

        builder.Keys.Cast<string>().Should().Contain(["Auth Token", "Replica Path", "Read Your Writes", "Sync Interval", "Tls"]);
        builder.AuthToken.Should().Be("secret");
        builder.ReplicaPath.Should().Be("replica.db");
        builder.ReadYourWrites.Should().BeFalse();
        builder.SyncInterval.Should().Be(15);
        builder.Tls.Should().BeTrue();
        builder.TryGetValue("TLS", out var tls).Should().BeTrue();
        tls.Should().Be(true);

        var projected = new AhtolaConnectionStringBuilder(builder.GetAhtolaConnectionString());
        projected.AuthToken.Should().Be("secret");
        projected.ReplicaPath.Should().Be("replica.db");
        projected.ReadYourWrites.Should().BeFalse();
        projected.SyncInterval.Should().Be(15);
        projected.Tls.Should().BeTrue();
        projected.DefaultTimeout.Should().Be(7);
    }

    [Test]
    public void RemoteClassifier_RecognizesTursoAndReplicaEndpoints()
    {
        AhtolaConnectionModeClassifier.Classify("turso://database.example")
            .Should().Be(AhtolaConnectionEndpointMode.RemoteHrana);
        AhtolaConnectionModeClassifier.Classify("libsql://database.example", "replica.db")
            .Should().Be(AhtolaConnectionEndpointMode.EmbeddedReplica);
        AhtolaConnectionModeClassifier.Classify("database.db")
            .Should().Be(AhtolaConnectionEndpointMode.Local);
    }

    [Test]
    public void DirectRemote_FacadeDelegatesCommandsTransactionsReadersAndBatches()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new SqliteConnection(
                "Data Source=https://example.test/db;Auth Token=token;Read Your Writes=true");
            connection.Open();

            connection.State.Should().Be(System.Data.ConnectionState.Open);
            connection.DataSource.Should().Be("https://example.test/db");
            connection.Capabilities.Mode.Should().Be(AhtolaConnectionMode.RemoteHrana);
            connection.Mode.Should().Be(AhtolaConnectionMode.RemoteHrana);
            connection.EndpointMode.Should().Be(AhtolaConnectionEndpointMode.RemoteHrana);
            connection.Capabilities.SupportsBackup.Should().BeFalse();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 42";
                using var reader = command.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(42);
                reader.GetName(0).Should().Be("value");
            }

            handler.Authorization.Should().Contain("token");

            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO t VALUES (1)";
                command.ExecuteNonQuery().Should().Be(1);
                transaction.Commit();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO t VALUES (2)";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                }

                reader.Close();
                reader.RecordsAffected.Should().Be(1);
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT last_insert_rowid()";
                command.ExecuteScalar().Should().Be(42L);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO t VALUES (5); SELECT 42; SELECT 42;";
                using var reader = command.ExecuteReader();
                reader.FieldCount.Should().Be(0);
                reader.NextResult().Should().BeTrue();
                reader.Read().Should().BeTrue();
                reader.GetFieldValue<int>(0).Should().Be(42);
                reader.NextResult().Should().BeTrue();
                reader.Read().Should().BeTrue();
                reader.GetFieldValueAsync<int>(0, CancellationToken.None).GetAwaiter().GetResult().Should().Be(42);
                reader.RecordsAffected.Should().Be(1);
            }
            handler.SqlStatements.Should().Contain("INSERT INTO t VALUES (5)");
            handler.SqlStatements.Should().NotContain(sql => sql.Contains(';'));

            using (var batch = connection.CreateBatch())
            {
                batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO t VALUES (2)"));
                batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO t VALUES (3)"));
                batch.ExecuteNonQuery().Should().Be(2);
                batch.BatchCommands[0].RecordsAffected.Should().Be(1);
                batch.BatchCommands[1].RecordsAffected.Should().Be(1);
            }

            using (var batch = connection.CreateBatch())
            {
                batch.BatchCommands.Add(new SqliteBatchCommand("SELECT 42"));
                batch.BatchCommands.Add(new SqliteBatchCommand("SELECT 42"));
                using var reader = batch.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(42);
                reader.NextResult().Should().BeTrue();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(42);
            }

            Action badSql = () =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "BAD";
                command.ExecuteNonQuery();
            };
            badSql.Should().Throw<SqliteException>().Which.Message.Should().Contain("remote syntax error");
            badSql.Should().Throw<SqliteRemoteException>()
                .Which.Classification.Should().Be(SqliteRemoteErrorClassification.Permanent);

            Action transient = () =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "TRANSIENT";
                command.ExecuteNonQuery();
            };
            var transientException = transient.Should().Throw<SqliteRemoteException>().Which;
            transientException.Classification.Should().Be(SqliteRemoteErrorClassification.Transient);
            SqliteRemoteExceptionClassifier.IsTransient(transientException).Should().BeTrue();
            connection.Close();
            connection.Open();

            Action createFunction = () => connection.CreateFunction("local_only", () => 1);
            createFunction.Should().Throw<NotSupportedException>()
                .Which.Message.Should().Contain("local");
            Action loadExtension = () => connection.LoadExtension("local_only");
            loadExtension.Should().Throw<NotSupportedException>();
            Action openBlob = () => new SqliteBlob(connection, "t", "value", 1);
            openBlob.Should().Throw<NotSupportedException>();
            connection.Close();
            connection.State.Should().Be(System.Data.ConnectionState.Closed);
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public void LocalFacade_RemainsManagedAndFunctional()
    {
        using var connection = new SqliteConnection(
            "Data Source=:memory:;Mode=Memory;Local Provider=Managed");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (7); SELECT id FROM t;";
        using var reader = command.ExecuteReader();
        while (reader.FieldCount == 0 && reader.NextResult())
        {
        }

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(7);
        connection.Capabilities.Mode.Should().Be(AhtolaConnectionMode.ManagedLocal);
    }

    [Test]
    public void DirectRemoteReadOnlyMode_FailsRatherThanSilentlyAllowingWrites()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new SqliteConnection(
                "Data Source=turso://database.example;Mode=ReadOnly;Auth Token=token;Pooling=False");
            Action open = connection.Open;
            open.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("server-side");
        }

        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public void RemoteAndReplicaHooks_AreRejectedBeforeOpenWithoutLeakingIntoLocalConnections()
    {
        using var remote = new SqliteConnection("Data Source=https://example.test/db;Auth Token=token");
        Action setAuthorizer = () => remote.SetAuthorizer(_ => SqliteAuthorizerResult.Ok);
        Action setUpdate = () => remote.SetUpdateHook(_ => { });
        Action setCommit = () => remote.SetCommitHook(() => true);
        Action setRollback = () => remote.SetRollbackHook(() => { });
        setAuthorizer.Should().Throw<NotSupportedException>();
        setUpdate.Should().Throw<NotSupportedException>();
        setCommit.Should().Throw<NotSupportedException>();
        setRollback.Should().Throw<NotSupportedException>();

        using var replica = new SqliteConnection(
            "Data Source=https://example.test/db;Replica Path=facade-hook-replica.db;Local Provider=Managed");
        Action replicaAuthorizer = () => replica.SetAuthorizer(_ => SqliteAuthorizerResult.Ok);
        replicaAuthorizer.Should().Throw<NotSupportedException>();

        using var local = new SqliteConnection("Data Source=:memory:;Mode=Memory;Local Provider=Managed");
        local.Open();
        using var command = local.CreateCommand();
        command.CommandText = "SELECT 1";
        command.ExecuteScalar().Should().Be(1L);
    }

    [Test]
    public async Task DirectRemoteFacade_DelegatesAsyncOperations()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            await using var connection = new SqliteConnection(
                "Data Source=https://example.test/db;Auth Token=token");
            await connection.OpenAsync();

            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "SELECT 42";
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt64(0).Should().Be(42);
                await transaction.CommitAsync();
            }

            await using var batch = connection.CreateBatch();
            batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO t VALUES (4)"));
            (await batch.ExecuteNonQueryAsync(CancellationToken.None)).Should().Be(1);
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public async Task DirectRemoteReader_UsesFacadeConversionsForTypedMaterialization()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new SqliteConnection("Data Source=https://example.test/db;Auth Token=token");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT typed_values";
            using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            reader.GetFieldValue<int>(0).Should().Be(7);
            reader.GetFieldValue<bool>(1).Should().BeTrue();
            reader.GetFieldValue<DateTime>(2).Should().Be(new DateTime(2024, 1, 2, 3, 4, 5));
            reader.GetFieldValue<DateTimeOffset>(3).Should().Be(
                new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)));
            reader.GetFieldValue<Guid>(4).Should().Be(new Guid("01234567-89ab-cdef-0123-456789abcdef"));
            reader.GetFieldValue<TimeSpan>(5).Should().Be(TimeSpan.FromMinutes(90));
            (await reader.GetFieldValueAsync<decimal>(6, CancellationToken.None)).Should().Be(12.50m);
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public async Task DirectRemoteReaderAsync_PropagatesCancellation()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new SqliteConnection("Data Source=https://example.test/db;Auth Token=token");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CANCEL";
            using var cancellation = new CancellationTokenSource();
            var execution = command.ExecuteReaderAsync(cancellation.Token);
            await handler.CancellationRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            command.Cancel();
            Func<Task> awaitExecution = async () => await execution;
            await awaitExecution.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public void RemoteRealParameters_AreSerializedAsInvariantHranaValues()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using (var direct = new AhtolaConnection("Data Source=https://example.test/db;Auth Token=token"))
            {
                direct.Open();
                using var command = direct.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES (?)";
                command.Parameters.Add(new AhtolaParameter(1.25d));
                command.ExecuteNonQuery().Should().Be(1);
            }

            using var connection = new SqliteConnection("Data Source=https://example.test/db;Auth Token=token");
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO t VALUES ($value)";
                command.Parameters.AddWithValue("$value", 2.5f);
                command.ExecuteNonQuery().Should().Be(1);
            }

            using (var batch = connection.CreateBatch())
            {
                var command = new SqliteBatchCommand("INSERT INTO t VALUES ($value)");
                command.Parameters.AddWithValue("$value", 3.75d);
                batch.BatchCommands.Add(command);
                batch.ExecuteNonQuery().Should().Be(1);
            }

            handler.RealArgumentKinds.Should().OnlyContain(kind => kind == JsonValueKind.Number);
            handler.RealArguments.Should().BeEquivalentTo([1.25d, 2.5d, 3.75d]);
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public async Task RemoteNonFiniteRealParameters_AreRejectedWithoutInvalidatingConnections()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            var values = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity };
            using (var direct = new AhtolaConnection("Data Source=https://example.test/db;Auth Token=token"))
            {
                direct.Open();
                foreach (var value in values)
                {
                    using var command = direct.CreateCommand();
                    command.CommandText = "INSERT INTO t VALUES (?)";
                    command.Parameters.Add(new AhtolaParameter(value));
                    Action execute = () => command.ExecuteNonQuery();
                    execute.Should().Throw<AhtolaParameterException>();
                    Func<Task> executeAsync = async () => await command.ExecuteNonQueryAsync();
                    await executeAsync.Should().ThrowAsync<AhtolaParameterException>();
                    direct.State.Should().Be(System.Data.ConnectionState.Open);
                }

                using var valid = direct.CreateCommand();
                valid.CommandText = "SELECT 42";
                valid.ExecuteScalar().Should().Be(42L);
            }

            using (var facade = new SqliteConnection("Data Source=https://example.test/db;Auth Token=token"))
            {
                facade.Open();
                foreach (var value in values)
                {
                    using var command = facade.CreateCommand();
                    command.CommandText = "INSERT INTO t VALUES ($value)";
                    command.Parameters.AddWithValue("$value", value);
                    Action execute = () => command.ExecuteNonQuery();
                    execute.Should().Throw<SqliteRemoteException>()
                        .Which.Classification.Should().Be(SqliteRemoteErrorClassification.Permanent);
                    Func<Task> executeAsync = async () => await command.ExecuteNonQueryAsync();
                    await executeAsync.Should().ThrowAsync<SqliteRemoteException>();
                    facade.State.Should().Be(System.Data.ConnectionState.Open);
                }

                using var valid = facade.CreateCommand();
                valid.CommandText = "SELECT 42";
                valid.ExecuteScalar().Should().Be(42L);
            }
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public void EmbeddedReplicaFacade_UsesManagedReplicaAndExposesSyncCapability()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sqlite-facade-replica-{Guid.NewGuid():N}.db");
        try
        {
            using (var local = new SqliteConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=False"))
            {
                local.Open();
                using var create = local.CreateCommand();
                create.CommandText = "CREATE TABLE values_table (value INTEGER); INSERT INTO values_table VALUES (9);";
                create.ExecuteNonQuery();
            }

            using var replica = new SqliteConnection(
                $"Data Source=https://example.test/cluster;Replica Path={path};Local Provider=Managed;Sync Interval=1;Pooling=False");
            replica.Open();
            replica.Capabilities.Mode.Should().Be(AhtolaConnectionMode.EmbeddedReplica);
            replica.Capabilities.SupportsSync.Should().BeTrue();
            replica.CanCreateBatch.Should().BeFalse();

            using (var command = replica.CreateCommand())
            {
                command.CommandText = "SELECT value FROM values_table;";
                using var reader = command.ExecuteReader();
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(9);
            }

            Action sync = () => replica.Sync();
            sync.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("bootstrap metadata");
            Action createBatch = () => replica.CreateBatch();
            createBatch.Should().Throw<NotSupportedException>();
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [Test]
    public void EmbeddedReplicaReadOnlyMode_DeniesWrites()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sqlite-facade-readonly-replica-{Guid.NewGuid():N}.db");
        try
        {
            using (var local = new SqliteConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=False"))
            {
                local.Open();
                using var setup = local.CreateCommand();
                setup.CommandText = "CREATE TABLE values_table (value INTEGER); INSERT INTO values_table VALUES (9);";
                setup.ExecuteNonQuery();
            }

            using var replica = new SqliteConnection(
                $"Data Source=https://example.test/cluster;Replica Path={path};Mode=ReadOnly;Local Provider=Managed;Pooling=False");
            replica.Open();
            using var read = replica.CreateCommand();
            read.CommandText = "SELECT value FROM values_table";
            read.ExecuteScalar().Should().Be(9L);

            Action write = () =>
            {
                using var command = replica.CreateCommand();
                command.CommandText = "INSERT INTO values_table VALUES (10)";
                command.ExecuteNonQuery();
            };
            write.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(8);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [Test]
    public async Task EmbeddedReplicaFacade_SyncAndSyncAsyncUseConfiguredTransport()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sqlite-facade-sync-replica-{Guid.NewGuid():N}.db");
        using var handler = new ReplicaSyncHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using (var local = new SqliteConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=False"))
            {
                local.Open();
                using var setup = local.CreateCommand();
                setup.CommandText = "CREATE TABLE values_table (value INTEGER);";
                setup.ExecuteNonQuery();
            }

            var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            File.WriteAllText(
                path + ".ahtola-replica-meta",
                $"version=2\nserver_revision_base64={Convert.ToBase64String(Encoding.UTF8.GetBytes("rev"))}\n"
                + $"database_sha256={fingerprint}\nclient_id={Guid.NewGuid():N}\n");

            using var replica = new SqliteConnection(
                $"Data Source=https://example.test/cluster;Replica Path={path};Local Provider=Managed;Pooling=False");
            replica.Open();
            replica.Sync();
            await replica.SyncAsync();
            handler.PullRequestCount.Should().Be(2);
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
            var directory = Path.GetDirectoryName(path)!;
            foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [Test]
    public void DirectRemoteBatch_ConditionsLaterStepsAndStopsAfterFailure()
    {
        using var handler = new FacadeRemoteHandler();
        var priorFactory = SqliteConnection.RemoteMessageHandlerFactory;
        SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new SqliteConnection("Data Source=https://example.test/db;Auth Token=token");
            connection.Open();
            using var batch = connection.CreateBatch();
            batch.BatchCommands.Add(new SqliteBatchCommand("INSERT INTO t VALUES (1)"));
            batch.BatchCommands.Add(new SqliteBatchCommand("FAIL"));
            batch.BatchCommands.Add(new SqliteBatchCommand("DELETE FROM t"));

            Action execute = () => batch.ExecuteNonQuery();
            execute.Should().Throw<SqliteRemoteException>();
            handler.BatchConditions.Should().Equal([null, "ok:0", "ok:1"]);
            handler.ExecutedBatchStatements.Should().Equal(
                "INSERT INTO t VALUES (1)",
                "FAIL");
        }
        finally
        {
            SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    private sealed class FacadeRemoteHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public List<string> SqlStatements { get; } = [];
        public List<double> RealArguments { get; } = [];
        public List<JsonValueKind> RealArgumentKinds { get; } = [];
        public List<string?> BatchConditions { get; } = [];
        public List<string> ExecutedBatchStatements { get; } = [];
        public TaskCompletionSource CancellationRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var requestEntry = document.RootElement.GetProperty("requests")[0];
            if (requestEntry.TryGetProperty("batch", out var batch))
            {
                var stepResults = new List<string>();
                var stepErrors = new List<string>();
                var previousSucceeded = true;
                foreach (var step in batch.GetProperty("steps").EnumerateArray())
                {
                    CaptureRealArguments(step.GetProperty("stmt"));
                    var batchSql = step.GetProperty("stmt").GetProperty("sql").GetString()!;
                    SqlStatements.Add(batchSql);
                    var conditional = step.TryGetProperty("condition", out var condition);
                    BatchConditions.Add(conditional
                        ? $"{condition.GetProperty("type").GetString()}:{condition.GetProperty("step").GetInt32()}"
                        : null);
                    if (conditional && !previousSucceeded)
                    {
                        stepResults.Add("null");
                        stepErrors.Add("null");
                        continue;
                    }

                    ExecutedBatchStatements.Add(batchSql);
                    if (string.Equals(batchSql, "FAIL", StringComparison.Ordinal))
                    {
                        stepResults.Add("null");
                        stepErrors.Add("""{"message":"remote batch failure","code":"SQLITE_ERROR"}""");
                        previousSucceeded = false;
                        continue;
                    }

                    stepResults.Add(batchSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                        ? """{"cols":[{"name":"value","decltype":"INTEGER"}],"rows":[[{"type":"integer","value":"42"}]],"affected_row_count":0}"""
                        : """{"cols":[],"rows":[],"affected_row_count":1}""");
                    stepErrors.Add("null");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":["""
                        + string.Join(",", stepResults)
                        + """],"step_errors":["""
                        + string.Join(",", stepErrors)
                        + """]}}}]}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            var sql = requestEntry
                .GetProperty("stmt").GetProperty("sql").GetString();
            CaptureRealArguments(requestEntry.GetProperty("stmt"));
            SqlStatements.Add(sql!);
            if (string.Equals(sql, "CANCEL", StringComparison.Ordinal))
            {
                CancellationRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (string.Equals(sql, "TRANSIENT", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = "temporarily unavailable",
                    Content = new StringContent("retry later", Encoding.UTF8, "text/plain"),
                };
            }
            var response = string.Equals(sql, "BAD", StringComparison.Ordinal)
                ? """{"results":[{"type":"error","error":{"message":"remote syntax error","code":"SQLITE_ERROR"}}]}"""
                : string.Equals(sql, "SELECT typed_values", StringComparison.Ordinal)
                    ? """{"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"integer"},{"name":"boolean"},{"name":"datetime"},{"name":"datetimeoffset"},{"name":"guid"},{"name":"timespan"},{"name":"decimal"}],"rows":[[{"type":"text","value":"7"},{"type":"text","value":"true"},{"type":"text","value":"2024-01-02 03:04:05"},{"type":"text","value":"2024-01-02T03:04:05+02:00"},{"type":"blob","base64":"Z0UjAauJ780BI0VniavN7w"},{"type":"text","value":"01:30:00"},{"type":"text","value":"12.50"}]],"affected_row_count":0}}}]}"""
                : sql!.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    ? """{"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"value","decltype":"INTEGER"}],"rows":[[{"type":"integer","value":"42"}]],"affected_row_count":0}}}]}"""
                    : """{"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[],"rows":[],"affected_row_count":1}}}]}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }

        private void CaptureRealArguments(JsonElement statement)
        {
            if (statement.TryGetProperty("args", out var arguments))
                foreach (var argument in arguments.EnumerateArray())
                    CaptureRealArgument(argument);

            if (statement.TryGetProperty("named_args", out var namedArguments))
                foreach (var argument in namedArguments.EnumerateArray())
                    CaptureRealArgument(argument.GetProperty("value"));
        }

        private void CaptureRealArgument(JsonElement argument)
        {
            if (argument.TryGetProperty("type", out var type)
                && type.GetString() == "float"
                && argument.TryGetProperty("value", out var value))
            {
                RealArgumentKinds.Add(value.ValueKind);
                RealArguments.Add(value.GetDouble());
            }
        }

    }

    private sealed class ReplicaSyncHandler : HttpMessageHandler
    {
        public int PullRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PullRequestCount++;
            // A delimited PullUpdatesResponse header: revision "rev", one page, raw encoding.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([9, 10, 3, (byte)'r', (byte)'e', (byte)'v', 16, 1, 26, 0]),
            };
            return Task.FromResult(response);
        }
    }
}
