using System.Text.Json;

using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Wire-level coverage for the persistent Hrana WebSocket transport, driven by an
/// in-memory <see cref="FakeWebSocket"/> and the scripted <see cref="FakeHranaServer"/>.
/// </summary>
/// <remarks>
/// The message shapes asserted here come from the authoritative libSQL/sqld specs
/// (<c>docs/HRANA_{1,2,3}_SPEC.md</c>), not from the pinned Turso engine, which has no
/// native Hrana WebSocket server.
/// </remarks>
public sealed class HranaWebSocketTransportTests
{
    private static readonly Uri Endpoint = new("wss://database.example");

    [Test]
    public async Task HandshakeOffersEveryJsonSubprotocolAndSendsHelloFirst()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server, authToken: "jwt-token");

        _ = await ExecuteAsync(client, "SELECT 1");

        server.OfferedSubProtocols.Should().Equal("hrana3", "hrana2", "hrana1");
        server.ObservedJwt.Should().Be("jwt-token");
        var first = JsonDocument.Parse(server.ReceivedMessages[0]).RootElement;
        first.GetProperty("type").GetString().Should().Be("hello");
        first.GetProperty("jwt").GetString().Should().Be("jwt-token");
        server.RequestTypes.Should().StartWith(["open_stream", "execute"]);
    }

    [Test]
    public async Task HelloCarriesAnExplicitNullJwtWhenNoTokenIsConfigured()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);

        _ = await ExecuteAsync(client, "SELECT 1");

        var hello = JsonDocument.Parse(server.ReceivedMessages[0]).RootElement;
        hello.TryGetProperty("jwt", out var jwt).Should().BeTrue("the spec models jwt as string|null, not as an optional field");
        jwt.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestCase("hrana3", 3)]
    [TestCase("hrana2", 2)]
    [TestCase("hrana1", 1)]
    [TestCase(null, 1)]
    [TestCase("", 1)]
    public async Task NegotiationMapsSubprotocolsToVersions(string? subProtocol, int expectedVersion)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        using var client = CreateClient(server);

        _ = await ExecuteAsync(client, "SELECT 1");

        client.NegotiatedWebSocketVersion.Should().Be(expectedVersion);
    }

    [TestCase("hrana3-protobuf")]
    [TestCase("hrana4")]
    [TestCase("mqtt")]
    public async Task NegotiationRejectsUnknownOrBinaryOnlySubprotocols(string subProtocol)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        using var client = CreateClient(server);

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>())
            .WithMessage("*unsupported WebSocket subprotocol*");
    }

    [Test]
    public async Task HelloErrorFailsTheConnectionWithTheServerMessage()
    {
        var server = new FakeHranaServer { HelloError = "bad token" };
        using var client = CreateClient(server, authToken: "jwt");

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*bad token*");
    }

    [Test]
    public async Task ExecutePreservesIntegerAsStringAndBlobEncoding()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);
        var parameters = new AhtolaParameterCollection();
        parameters.Add(9007199254740993L);
        parameters.Add(new byte[] { 1, 2, 3 });
        parameters.Add(1.5d);
        parameters.Add("text");

        var result = await client.ExecuteAsync(
            "SELECT ?, ?, ?, ?",
            parameters,
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        result.Rows.Should().ContainSingle();
        result.Rows[0][0].GetInt64().Should().Be(1);
        var request = server.ReceivedMessages.Select(message => JsonDocument.Parse(message))
            .First(document => document.RootElement.TryGetProperty("request", out var candidate)
                && candidate.GetProperty("type").GetString() == "execute");
        var args = request.RootElement.GetProperty("request").GetProperty("stmt").GetProperty("args");
        args[0].GetProperty("type").GetString().Should().Be("integer");
        args[0].GetProperty("value").GetString().Should().Be("9007199254740993");
        args[1].GetProperty("type").GetString().Should().Be("blob");
        args[1].GetProperty("base64").GetString().Should().Be(Convert.ToBase64String([1, 2, 3]));
        args[2].GetProperty("type").GetString().Should().Be("float");
        args[2].GetProperty("value").GetDouble().Should().Be(1.5d);
        args[3].GetProperty("type").GetString().Should().Be("text");
        request.Dispose();
    }

    [Test]
    public async Task BatchSendsOneRequestAndProjectsEveryStepResult()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);

        var results = await client.ExecuteBatchAsync(
            [new AhtolaBatchCommand("INSERT INTO t VALUES (1)"), new AhtolaBatchCommand("INSERT INTO t VALUES (2)")],
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        results.Should().HaveCount(2);
        server.RequestTypes.Should().Equal("open_stream", "batch", "close_stream");
        server.ExecutedSql.Should().Equal("INSERT INTO t VALUES (1)", "INSERT INTO t VALUES (2)");
    }

    [Test]
    public async Task UnknownJsonFieldsAreIgnored()
    {
        var server = new FakeHranaServer { PaddingBytes = 64 };
        using var client = CreateClient(server);

        var result = await ExecuteAsync(client, "SELECT 1");

        result.Rows.Should().ContainSingle();
    }

    [Test]
    public async Task FragmentedServerMessagesAreReassembled()
    {
        var server = new FakeHranaServer { FragmentSize = 7 };
        using var client = CreateClient(server);

        var result = await ExecuteAsync(client, "SELECT 1");

        result.Rows.Should().ContainSingle();
        server.Socket!.SawConcurrentReceive.Should().BeFalse();
    }

    [Test]
    public async Task OversizedServerMessagesTerminateTheGeneration()
    {
        var server = new FakeHranaServer { PaddingBytes = 64 * 1024 };
        using var client = CreateClient(server, options: new AhtolaHranaWebSocketOptions { MaxMessageBytes = 8 * 1024 });

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*Ws Max Message Bytes*");
        server.Socket!.SentCloseStatus.Should().Be(System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig);
    }

    [Test]
    public async Task BinaryFramesAreRejectedOnAJsonSubprotocol()
    {
        var server = new FakeHranaServer { SendBinaryFrame = true };
        using var client = CreateClient(server);

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*binary frame*");
        server.Socket!.SentCloseStatus.Should().Be(System.Net.WebSockets.WebSocketCloseStatus.InvalidMessageType);
    }

    [Test]
    public async Task UnknownMessageDiscriminatorsTerminateTheGeneration()
    {
        var server = new FakeHranaServer { SendUnknownDiscriminator = true };
        using var client = CreateClient(server);

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*unknown message type*");
    }

    [Test]
    public async Task ResponsesForUnknownRequestIdsTerminateTheGeneration()
    {
        var server = new FakeHranaServer { AnswerUnknownRequestId = true };
        using var client = CreateClient(server);

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*unknown request id*");
    }

    [Test]
    public async Task ServerCloseFramesFailPendingRequests()
    {
        var server = new FakeHranaServer { CloseOnRequestType = "execute" };
        using var client = CreateClient(server);

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*closed by the server*");
    }

    [Test]
    public async Task ResponseErrorsSurfaceAsRemoteSqlExceptionsAndKeepTheConnection()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["execute"] = ("SQLITE_CONSTRAINT", "UNIQUE constraint failed");
        using var client = CreateClient(server);

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaRemoteSqlException>())
            .WithMessage("*UNIQUE constraint failed*");
        server.RequestErrors.Clear();

        // The socket is still healthy: an application error is not a protocol violation.
        var recovered = await ExecuteAsync(client, "SELECT 2");
        recovered.Rows.Should().ContainSingle();
        server.ConnectAttempts.Should().Be(1);
    }

    [Test]
    public async Task OutOfOrderResponsesAcrossStreamsAreCorrelatedByRequestId()
    {
        var server = new FakeHranaServer { ReplyToHeldRequestsInReverseOrder = true };
        var connection = await AhtolaHranaWebSocketConnection.ConnectAsync(
            Endpoint,
            authToken: null,
            server,
            AhtolaHranaWebSocketOptions.Default,
            generation: 1,
            CancellationToken.None);
        await using (connection)
        {
            var firstStream = connection.AllocateStreamId();
            var secondStream = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(firstStream), TimeSpan.Zero, CancellationToken.None);
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(secondStream), TimeSpan.Zero, CancellationToken.None);

            server.HoldRequestTypes.Add("execute");
            var first = connection.SendRequestAsync(
                HranaRequest.ForExecute(firstStream, BuildStatement("SELECT 1")),
                TimeSpan.Zero,
                CancellationToken.None);
            var second = connection.SendRequestAsync(
                HranaRequest.ForExecute(secondStream, BuildStatement("SELECT 2")),
                TimeSpan.Zero,
                CancellationToken.None);
            await WaitForAsync(() => server.HeldRequestCount == 2);
            server.ReleaseHeldRequests();

            // The server answered the second request first; each caller must still receive
            // the response for its own request id.
            var firstResult = (await first).Result.Deserialize(
                AhtolaHranaJsonContext.Default.RemoteStatementResult)!;
            var secondResult = (await second).Result.Deserialize(
                AhtolaHranaJsonContext.Default.RemoteStatementResult)!;
            firstResult.LastInsertRowId.GetString().Should().Be(firstStream.ToString());
            secondResult.LastInsertRowId.GetString().Should().Be(secondStream.ToString());
        }

        server.Socket!.SawConcurrentSend.Should().BeFalse();
        server.Socket.SawConcurrentReceive.Should().BeFalse();
    }

    [Test]
    public async Task RequestsOnOneStreamKeepTheirIssueOrder()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);

        for (var index = 0; index < 5; index++)
            _ = await ExecuteAsync(client, $"SELECT {index}", closeAfter: false);

        server.ExecutedSql.Should().Equal("SELECT 0", "SELECT 1", "SELECT 2", "SELECT 3", "SELECT 4");
        server.OpenedStreamIds.Should().ContainSingle("the session stream is reused until it is closed");
    }

    [Test]
    public async Task ConcurrentOperationsOnOneAdoConnectionAreRejectedByTheRequestGate()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        using var client = CreateClient(server);

        var pending = client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 0,
            closeAfter: false,
            CancellationToken.None);
        await WaitForAsync(() => server.HeldRequestCount == 1);

        var second = async () => await ExecuteAsync(client, "SELECT 2");
        await second.Should().ThrowAsync<InvalidOperationException>();

        server.ReleaseHeldRequests();
        (await pending).Rows.Should().ContainSingle();
    }

    [Test]
    public async Task CancellationOnlyAbandonsTheCallerAndALateResponseIsDiscarded()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        using var client = CreateClient(server);
        using var cancellation = new CancellationTokenSource();

        var pending = client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 0,
            closeAfter: false,
            cancellation.Token);
        await WaitForAsync(() => server.HeldRequestCount == 1);
        await cancellation.CancelAsync();

        var awaitPending = async () => await pending;
        await awaitPending.Should().ThrowAsync<OperationCanceledException>();

        // The late response for the abandoned id must be dropped, not treated as a violation.
        server.ReleaseHeldRequests();
        server.HoldRequestTypes.Clear();
        var recovered = await ExecuteAsync(client, "SELECT 2");
        recovered.Rows.Should().ContainSingle();
        server.ConnectAttempts.Should().Be(1);
        server.Socket!.Aborted.Should().BeFalse();
    }

    [Test]
    public async Task CommandTimeoutCancelsOnlyTheCallerWait()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        using var client = CreateClient(server);

        var execute = async () => await client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 1,
            closeAfter: false,
            CancellationToken.None);

        await execute.Should().ThrowAsync<OperationCanceledException>();
        server.Socket!.Aborted.Should().BeFalse();
    }

    [Test]
    public async Task CursorPagesThroughFetchCursorAndClosesDeterministically()
    {
        var server = new FakeHranaServer { CursorPageLimit = 2 };
        server.Rows.Clear();
        server.Rows.AddRange(["1", "2", "3", "4", "5"]);
        using var client = CreateClient(server);

        var execution = await client.ExecuteCursorAsync(
            "SELECT value FROM t",
            new AhtolaParameterCollection(),
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        execution.Cursor.Should().NotBeNull();
        var cursor = execution.Cursor!;
        cursor.Columns.Should().ContainSingle(column => column.Name == "value");
        var values = new List<long>();
        while (await cursor.ReadRowAsync(CancellationToken.None) is { } row)
            values.Add(row[0].GetInt64());
        await cursor.DisposeAsync();

        values.Should().Equal(1, 2, 3, 4, 5);
        server.FetchCursorCalls.Should().BeGreaterThan(1, "paging must use several fetch_cursor round trips");
        server.RequestTypes.Should().Contain("open_cursor");
        server.RequestTypes.Should().Contain("close_cursor");
        server.RequestTypes.Should().Contain("close_stream");
    }

    [Test]
    public async Task CursorRequestsRespectTheConfiguredPageSize()
    {
        var server = new FakeHranaServer { CursorPageLimit = 64 };
        server.Rows.Clear();
        server.Rows.AddRange(Enumerable.Range(1, 10).Select(value => value.ToString()));
        using var client = CreateClient(server, options: new AhtolaHranaWebSocketOptions { CursorPageSize = 3 });

        var execution = await client.ExecuteCursorAsync(
            "SELECT value FROM t",
            new AhtolaParameterCollection(),
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);
        var cursor = execution.Cursor!;
        while (await cursor.ReadRowAsync(CancellationToken.None) is not null)
        {
        }
        await cursor.DisposeAsync();

        var fetch = server.ReceivedMessages
            .Select(message => JsonDocument.Parse(message))
            .First(document => document.RootElement.TryGetProperty("request", out var request)
                && request.GetProperty("type").GetString() == "fetch_cursor");
        fetch.RootElement.GetProperty("request").GetProperty("max_count").GetInt32().Should().Be(3);
        fetch.Dispose();
    }

    [TestCase("hrana2")]
    [TestCase("hrana1")]
    public async Task CursorsFallBackToABufferedExecuteBeforeHrana3(string subProtocol)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        server.Rows.Clear();
        server.Rows.AddRange(["1", "2"]);
        using var client = CreateClient(server);

        var execution = await client.ExecuteCursorAsync(
            "SELECT value FROM t",
            new AhtolaParameterCollection(),
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        execution.Cursor.Should().BeNull();
        execution.BufferedResult!.Rows.Should().HaveCount(2);
        server.RequestTypes.Should().NotContain("open_cursor");
        server.RequestTypes.Should().Contain("execute");
    }

    [Test]
    public async Task ParallelExecutionsAreSerializedAndRemainCorrelated()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);
        _ = await ExecuteAsync(client, "SELECT 0");

        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, AhtolaHranaWebSocketOptions.Default, server);
        await using (transport)
        {
            var tasks = Enumerable.Range(0, 32)
                .Select(index => transport.ExecuteAsync(
                    BuildStatement($"SELECT {index}"),
                    commandTimeout: 30,
                    closeAfter: false,
                    CancellationToken.None))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            results.Should().OnlyContain(result => result.Rows.Count == 1);
        }

        server.Sockets.Should().OnlyContain(socket => !socket.SawConcurrentSend && !socket.SawConcurrentReceive);
    }

    [Test]
    public async Task DisposalClosesTheStreamAndTheSocket()
    {
        var server = new FakeHranaServer();
        var client = CreateClient(server);
        _ = await ExecuteAsync(client, "SELECT 1", closeAfter: false);
        client.HasOpenSession.Should().BeTrue();

        client.Dispose();

        server.ClosedStreamIds.Should().NotBeEmpty();
        server.Socket!.CloseOutputSent.Should().BeTrue();
    }

    [Test]
    public void DisposalWithoutAnyOperationDoesNotConnect()
    {
        var server = new FakeHranaServer();
        var client = CreateClient(server);

        client.Dispose();

        server.ConnectAttempts.Should().Be(0);
    }

    [Test]
    public async Task StoredSqlSequenceDescribeAndAutocommitUseTheOfficialShapes()
    {
        var server = new FakeHranaServer();
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, AhtolaHranaWebSocketOptions.Default, server);
        await using (transport)
        {
            var sqlId = await transport.StoreSqlAsync("SELECT :id", commandTimeout: 30, CancellationToken.None);
            await transport.RunSequenceAsync("SELECT 1; SELECT 2;", sqlId: null, commandTimeout: 30, closeAfter: false, CancellationToken.None);
            var describe = await transport.DescribeAsync(sql: null, sqlId, commandTimeout: 30, closeAfter: false, CancellationToken.None);
            var autocommit = await transport.GetAutocommitAsync(commandTimeout: 30, CancellationToken.None);
            await transport.CloseSqlAsync(sqlId, commandTimeout: 30, CancellationToken.None);

            describe.Parameters.Should().ContainSingle(parameter => parameter.Name == ":id");
            describe.Columns.Should().ContainSingle(column => column.Name == "value");
            describe.IsReadOnly.Should().BeTrue();
            autocommit.Should().BeTrue();
        }

        server.RequestTypes.Should().Contain(["store_sql", "sequence", "describe", "get_autocommit", "close_sql"]);
    }

    [TestCase("hrana2", "fetch_cursor")]
    [TestCase("hrana1", "describe")]
    public async Task RequestsBelowTheNegotiatedVersionFailClosed(string subProtocol, string requestType)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, AhtolaHranaWebSocketOptions.Default, server);
        await using (transport)
        {
            Func<Task> request = requestType == "describe"
                ? async () => await transport.DescribeAsync("SELECT 1", sqlId: null, 30, closeAfter: false, CancellationToken.None)
                : async () => await transport.GetAutocommitAsync(30, CancellationToken.None);

            (await request.Should().ThrowAsync<AhtolaException>()).WithMessage("*does not support*");
        }
    }

    [Test]
    public async Task KeepAliveSettingsReachTheConnector()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(
            server,
            options: new AhtolaHranaWebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(11),
                KeepAliveTimeout = TimeSpan.FromSeconds(3),
            });

        _ = await ExecuteAsync(client, "SELECT 1");

        server.ObservedOptions!.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(11));
        server.ObservedOptions.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task LargeOutboundMessagesAreChunkedAndReassembled()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);
        var sql = "SELECT '" + new string('a', 200_000) + "'";

        _ = await ExecuteAsync(client, sql);

        server.ExecutedSql.Should().ContainSingle().Which.Should().HaveLength(sql.Length);
        server.Socket!.SendCallCount.Should().BeGreaterThan(3, "a 200 KB message must be sent as several 64 KB fragments");
        server.Socket.SawConcurrentSend.Should().BeFalse();
    }

    [Test]
    public async Task OutboundMessagesAboveTheConfiguredCapAreRefusedBeforeSending()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server, options: new AhtolaHranaWebSocketOptions { MaxMessageBytes = 16 * 1024 });
        var sql = "SELECT '" + new string('a', 64 * 1024) + "'";

        var execute = async () => await ExecuteAsync(client, sql);

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*exceeds the configured Ws Max Message Bytes*");
    }

    [Test]
    public async Task DisposalFailsRequestsThatAreStillPending()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        var client = CreateClient(server);

        var pending = client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 0,
            closeAfter: false,
            CancellationToken.None);
        await WaitForAsync(() => server.HeldRequestCount == 1);

        client.Dispose();

        var awaitPending = async () => await pending;
        await awaitPending.Should().ThrowAsync<Exception>();
        server.Socket!.State.Should().NotBe(System.Net.WebSockets.WebSocketState.Open);
    }

    [Test]
    public void TheProductionConnectorConfiguresKeepAliveAndKeepsPlatformCertificateValidation()
    {
        using var socket = AhtolaClientWebSocketConnector.CreateSocket(
            AhtolaHranaWireProtocol.JsonSubProtocols,
            new AhtolaHranaWebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(17),
                KeepAliveTimeout = TimeSpan.FromSeconds(5),
            });

        socket.Options.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(17));
        socket.Options.RemoteCertificateValidationCallback.Should()
            .BeNull("certificate validation must stay with the platform; no bypass is installed");
#if NET9_0_OR_GREATER
        socket.Options.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(5));
#endif
    }

    [Test]
    public async Task AReceiveStaysOutstandingSoKeepAlivePongsAreProcessed()
    {
        var server = new FakeHranaServer();
        using var client = CreateClient(server);

        _ = await ExecuteAsync(client, "SELECT 1", closeAfter: false);

        await WaitForAsync(() => server.Socket!.HasOutstandingReceive);
        server.Socket!.HasOutstandingReceive.Should()
            .BeTrue("the runtime only processes keep-alive pongs while a receive is pending");
    }

    [Test]
    public void TheProductionConnectorNeverOffersTheProtobufSubprotocol()
    {
        AhtolaHranaWireProtocol.JsonSubProtocols.Should().Equal("hrana3", "hrana2", "hrana1");
        AhtolaHranaWireProtocol.JsonSubProtocols.Should().NotContain("hrana3-protobuf");
    }

    [Test]
    public async Task ConnectionEstablishmentRetriesAreBounded()
    {
        var server = new FakeHranaServer { FailConnectAttempts = 2 };
        using var client = CreateClient(
            server,
            options: new AhtolaHranaWebSocketOptions { ConnectAttempts = 3, ConnectRetryDelay = TimeSpan.FromMilliseconds(1) });

        var result = await ExecuteAsync(client, "SELECT 1");

        result.Rows.Should().ContainSingle();
        server.ConnectAttempts.Should().Be(3);
    }

    [Test]
    public async Task ExhaustedConnectionAttemptsFailTheOperation()
    {
        var server = new FakeHranaServer { FailConnectAttempts = 10 };
        using var client = CreateClient(
            server,
            options: new AhtolaHranaWebSocketOptions { ConnectAttempts = 2, ConnectRetryDelay = TimeSpan.FromMilliseconds(1) });

        var execute = async () => await ExecuteAsync(client, "SELECT 1");

        (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*after 2 attempt(s)*");
        server.ConnectAttempts.Should().Be(2);
    }

    internal static AhtolaRemoteClient CreateClient(
        FakeHranaServer server,
        string? authToken = null,
        AhtolaHranaWebSocketOptions? options = null,
        Uri? endpoint = null)
        => new(
            endpoint ?? Endpoint,
            authToken,
            options ?? AhtolaHranaWebSocketOptions.Default,
            remoteEncryption: null,
            server);

    internal static Task<RemoteStatementResult> ExecuteAsync(
        AhtolaRemoteClient client,
        string sql,
        bool closeAfter = true)
        => client.ExecuteAsync(
            sql,
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter,
            CancellationToken.None);

    internal static RemoteStatement BuildStatement(string sql)
    {
        var statement = new RemoteStatement { Sql = sql, WantRows = true };
        return statement;
    }

    internal static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The scripted Hrana server did not reach the expected state in time.");
    }
}
