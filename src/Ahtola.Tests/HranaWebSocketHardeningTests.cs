using System.Diagnostics;
using System.Net.WebSockets;

using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for the hardening review of the Hrana WebSocket transport.
/// </summary>
/// <remarks>
/// Every test here pins one specific failure mode that used to be reachable: a close frame
/// racing a wedged send, a leaked <c>stream_id</c>/<c>cursor_id</c> after caller
/// cancellation, an evicted tombstone turning a valid late reply into a protocol abort, an
/// <c>is_autocommit</c> condition reaching a pre-v3 server, a malformed payload degrading
/// into a default value, a silently skipped nested discriminator, a stranded stream after an
/// application error, a stream minted before a version check, an undetected half-open peer,
/// and a synchronous dispose returning while the socket was still live.
/// </remarks>
public sealed class HranaWebSocketHardeningTests
{
    private static readonly Uri Endpoint = new("wss://database.example");

    private static AhtolaHranaWebSocketOptions FastOptions(
        TimeSpan? closeTimeout = null,
        TimeSpan? keepAliveInterval = null,
        TimeSpan? keepAliveTimeout = null,
        TimeSpan? halfOpenTimeout = null,
        int? maxTombstones = null,
        int? sendQueueCapacity = null)
        => new()
        {
            CloseTimeout = closeTimeout ?? TimeSpan.FromMilliseconds(250),
            ConnectRetryDelay = TimeSpan.FromMilliseconds(1),
            KeepAliveInterval = keepAliveInterval ?? TimeSpan.FromSeconds(30),
            KeepAliveTimeout = keepAliveTimeout ?? TimeSpan.FromSeconds(20),
            HalfOpenTimeout = halfOpenTimeout ?? TimeSpan.Zero,
            MaxCancelledRequestTombstones = maxTombstones ?? 65536,
            SendQueueCapacity = sendQueueCapacity ?? 128,
        };

    private static Task<AhtolaHranaWebSocketConnection> ConnectAsync(
        FakeHranaServer server,
        AhtolaHranaWebSocketOptions? options = null)
        => AhtolaHranaWebSocketConnection.ConnectAsync(
            Endpoint,
            authToken: null,
            server,
            (options ?? FastOptions()).Validate(),
            generation: 1,
            CancellationToken.None);

    // ---------------------------------------------------------------------------------
    // 1. CloseOutputAsync must never race a blocked send.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task DisposalAbortsInsteadOfSendingACloseFrameWhileASendIsWedged()
    {
        // The hello is send #1; the request below is send #2 and never completes, exactly
        // like a peer whose TCP window has closed for good.
        var server = new FakeHranaServer { BlockSendsAfter = 1 };
        var connection = await ConnectAsync(server);
        var socket = server.Socket!;

        var wedged = connection.SendRequestAsync(
            HranaRequest.ForOpenStream(connection.AllocateStreamId()),
            TimeSpan.Zero,
            CancellationToken.None);
        await WaitForAsync(() => socket.SendCallCount >= 2);

        await connection.DisposeAsync();

        socket.SawCloseOutputDuringSend.Should()
            .BeFalse("CloseOutputAsync is itself a send and must never overlap a pending SendAsync");
        socket.CloseOutputSent.Should()
            .BeFalse("a wedged send loop means the close frame has to be skipped entirely");
        socket.SawConcurrentSend.Should().BeFalse();
        socket.Aborted.Should().BeTrue("aborting is the only way to unblock a wedged send");
        socket.Disposed.Should().BeTrue();

        var faulted = async () => await wedged;
        await faulted.Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task AProtocolViolationAbortsInsteadOfSendingACloseFrameWhileASendIsWedged()
    {
        var server = new FakeHranaServer { BlockSendsAfter = 1 };
        var connection = await ConnectAsync(server);
        var socket = server.Socket!;

        var wedged = connection.SendRequestAsync(
            HranaRequest.ForOpenStream(connection.AllocateStreamId()),
            TimeSpan.Zero,
            CancellationToken.None);
        await WaitForAsync(() => socket.SendCallCount >= 2);

        // The receive loop now hits a protocol violation while the send loop is stuck.
        socket.Push("""{"type":"totally_unknown"}""");

        await WaitForAsync(() => !connection.IsAlive);
        socket.SawCloseOutputDuringSend.Should().BeFalse();
        socket.CloseOutputSent.Should().BeFalse();
        connection.Fault.Should().BeOfType<AhtolaHranaProtocolException>();

        var faulted = async () => await wedged;
        await faulted.Should().ThrowAsync<Exception>();
        await connection.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------
    // 2. Provisional open_stream / open_cursor lifecycle.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task AnAbandonedOpenStreamClosesTheStreamWhenTheServerAnswersLate()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("open_stream");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            using var cancellation = new CancellationTokenSource();
            var execute = transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                commandTimeout: 30,
                closeAfter: true,
                cancellation.Token);

            await WaitForAsync(() => server.HeldRequestCount == 1);
            await cancellation.CancelAsync();
            var cancelled = async () => await execute;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();

            // The server was already creating the stream; the late success must not leak it.
            server.ReleaseHeldRequests();
            await WaitForAsync(() => server.ClosedStreamIds.Count == 1);
            server.ClosedStreamIds.Should().Equal(server.OpenedStreamIds);
        }
    }

    [Test]
    public async Task AnAbandonedOpenCursorClosesTheCursorWhenTheServerAnswersLate()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("open_cursor");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            using var cancellation = new CancellationTokenSource();
            var batch = new RemoteBatch
            {
                Steps = [new RemoteBatchStep { Statement = new RemoteStatement { Sql = "SELECT 1", WantRows = true } }],
            };
            var open = transport.OpenCursorAsync(batch, commandTimeout: 30, cancellation.Token);

            await WaitForAsync(() => server.HeldRequestCount == 1);
            await cancellation.CancelAsync();
            var cancelled = async () => await open;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();

            server.ReleaseHeldRequests();
            await WaitForAsync(() => server.ClosedCursorIds.Count == 1);
            server.ClosedCursorIds.Should().Equal(server.OpenedCursorIds);
        }
    }

    [Test]
    public async Task AFailedLifecycleCompensationRetiresTheGeneration()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("open_stream");
        server.RequestErrors["close_stream"] = ("INTERNAL", "cannot close");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            using var cancellation = new CancellationTokenSource();
            var execute = transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                commandTimeout: 30,
                closeAfter: true,
                cancellation.Token);

            await WaitForAsync(() => server.HeldRequestCount == 1);
            await cancellation.CancelAsync();
            var cancelled = async () => await execute;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();

            server.ReleaseHeldRequests();

            // The handle could not be released, so the only way to reclaim it is to drop the
            // connection: a leaked server-side stream is worse than a lost generation.
            await WaitForAsync(() => server.Socket!.Aborted);
            server.Socket!.Aborted.Should().BeTrue();
        }
    }

    // ---------------------------------------------------------------------------------
    // 3. Cancelled-request tombstones stay correct for the whole generation.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task LateRepliesStayDiscardableAfterFarMoreCancellationsThanAnyFifoWindow()
    {
        const int Cancellations = 1200;

        var server = new FakeHranaServer { ReplyToHeldRequestsInReverseOrder = true };
        server.HoldRequestTypes.Add("execute");
        var connection = await ConnectAsync(server);
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(streamId), TimeSpan.Zero, CancellationToken.None);

            using var cancellation = new CancellationTokenSource();
            var abandoned = new List<Task<HranaResponse>>(Cancellations);
            for (var index = 0; index < Cancellations; index++)
            {
                abandoned.Add(connection.SendRequestAsync(
                    HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                    TimeSpan.Zero,
                    cancellation.Token));
            }

            await WaitForAsync(() => server.HeldRequestCount == Cancellations, timeoutMilliseconds: 30000);
            await cancellation.CancelAsync();
            foreach (var request in abandoned)
            {
                var cancelled = async () => await request;
                await cancelled.Should().ThrowAsync<OperationCanceledException>();
            }

            connection.CancelledRequestCount.Should().Be(Cancellations);

            // Every held reply now arrives, oldest last. A FIFO tombstone window would have
            // evicted the earliest ids long ago and aborted the generation as "unknown id".
            server.ReleaseHeldRequests();
            await WaitForAsync(() => connection.OutstandingRequestCount == 0);
            await Task.Delay(100);

            connection.IsAlive.Should().BeTrue("valid late replies must never become unknown-id aborts");
            connection.Fault.Should().BeNull();

            // The generation is still usable end to end.
            server.HoldRequestTypes.Clear();
            var followUp = await connection.SendRequestAsync(
                HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 2", WantRows = true }),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            followUp.Type.Should().Be("execute");
        }
    }

    [Test]
    public async Task ExhaustingTheTombstoneBudgetRetiresTheGenerationInsteadOfForgetting()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        var connection = await ConnectAsync(server, FastOptions(maxTombstones: 4));
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(streamId), TimeSpan.Zero, CancellationToken.None);

            using var cancellation = new CancellationTokenSource();
            var abandoned = new List<Task<HranaResponse>>();
            for (var index = 0; index < 5; index++)
            {
                abandoned.Add(connection.SendRequestAsync(
                    HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                    TimeSpan.Zero,
                    cancellation.Token));
            }

            await WaitForAsync(() => server.HeldRequestCount == 5);
            await cancellation.CancelAsync();
            foreach (var request in abandoned)
            {
                var cancelled = async () => await request;
                await cancelled.Should().ThrowAsync<OperationCanceledException>();
            }

            await WaitForAsync(() => !connection.IsAlive);
            connection.Fault.Should().NotBeNull();
            connection.Fault!.Message.Should().Contain("abandoned more than 4 requests");
        }
    }

    // ---------------------------------------------------------------------------------
    // 4. is_autocommit batch conditions are gated recursively, before any send.
    // ---------------------------------------------------------------------------------

    [TestCase("hrana1")]
    [TestCase("hrana2")]
    public async Task NestedIsAutocommitConditionsAreRejectedBeforeAnythingIsSent(string subProtocol)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        using var client = new AhtolaRemoteClient(Endpoint, authToken: null, FastOptions(), remoteEncryption: null, server);

        var batch = async () => await client.ExecuteBatchAsync(
            [
                new AhtolaBatchCommand("INSERT INTO t VALUES (1)"),
                new AhtolaBatchCommand("INSERT INTO t VALUES (2)")
                {
                    // The guarded replica-push shape: is_autocommit nested inside not().
                    RemoteCondition = AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit),
                },
            ],
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        (await batch.Should().ThrowAsync<AhtolaException>())
            .WithMessage("*does not support the 'is_autocommit' batch condition*");
        server.RequestTypes.Should().NotContain("batch");
        server.RequestTypes.Should().NotContain("open_stream", "the gate runs before a stream is minted");
    }

    [Test]
    public async Task DeeplyNestedIsAutocommitConditionsAreStillDetected()
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = "hrana2" };
        using var client = new AhtolaRemoteClient(Endpoint, authToken: null, FastOptions(), remoteEncryption: null, server);

        var buried = AhtolaRemoteBatchCondition.Or(
            AhtolaRemoteBatchCondition.StepSucceeded(0),
            AhtolaRemoteBatchCondition.And(
                AhtolaRemoteBatchCondition.StepFailed(0),
                AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit)));

        var batch = async () => await client.ExecuteBatchAsync(
            [
                new AhtolaBatchCommand("INSERT INTO t VALUES (1)"),
                new AhtolaBatchCommand("INSERT INTO t VALUES (2)") { RemoteCondition = buried },
            ],
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        (await batch.Should().ThrowAsync<AhtolaException>()).WithMessage("*is_autocommit*");
        server.RequestTypes.Should().NotContain("batch");
    }

    [TestCase("hrana1")]
    [TestCase("hrana2")]
    [TestCase("hrana3")]
    public async Task ConditionsDefinedByHrana1And2StillRunOnEveryVersion(string subProtocol)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        using var client = new AhtolaRemoteClient(Endpoint, authToken: null, FastOptions(), remoteEncryption: null, server);

        var results = await client.ExecuteBatchAsync(
            [
                new AhtolaBatchCommand("INSERT INTO t VALUES (1)"),
                new AhtolaBatchCommand("INSERT INTO t VALUES (2)")
                {
                    RemoteCondition = AhtolaRemoteBatchCondition.And(
                        AhtolaRemoteBatchCondition.StepSucceeded(0),
                        AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.StepFailed(0))),
                },
            ],
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        results.Should().HaveCount(2);
        server.RequestTypes.Should().Contain("batch");
    }

    [Test]
    public async Task IsAutocommitConditionsAreAcceptedOnHrana3()
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = "hrana3" };
        using var client = new AhtolaRemoteClient(Endpoint, authToken: null, FastOptions(), remoteEncryption: null, server);

        var results = await client.ExecuteBatchAsync(
            [
                new AhtolaBatchCommand("INSERT INTO t VALUES (1)")
                {
                    RemoteCondition = AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit),
                },
            ],
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        results.Should().HaveCount(1);
        server.RequestTypes.Should().Contain("batch");
    }

    // ---------------------------------------------------------------------------------
    // 5 & 6. Malformed payloads and unknown nested discriminators fault the generation.
    // ---------------------------------------------------------------------------------

    [TestCase(
        "get_autocommit",
        """{"type":"response_ok","request_id":ID,"response":{"type":"get_autocommit"}}""",
        "is_autocommit",
        TestName = "GetAutocommitWithoutTheFlag")]
    [TestCase(
        "execute",
        """{"type":"response_ok","request_id":ID,"response":{"type":"execute"}}""",
        "result",
        TestName = "ExecuteWithoutAResult")]
    [TestCase(
        "execute",
        """{"type":"response_ok","request_id":ID,"response":{"type":"execute","result":{"rows":[],"affected_row_count":0}}}""",
        "cols",
        TestName = "ExecuteResultWithoutCols")]
    [TestCase(
        "execute",
        """{"type":"response_ok","request_id":ID,"response":{"type":"execute","result":{"cols":[{"name":"a"},{"name":"b"}],"rows":[[{"type":"null"}]],"affected_row_count":0}}}""",
        "values for 2 columns",
        TestName = "ExecuteRowWidthMismatch")]
    [TestCase(
        "execute",
        """{"type":"response_ok","request_id":ID,"response":{"type":"execute","result":{"cols":[{"name":"a"}],"rows":[[{"type":"decimal","value":"1"}]],"affected_row_count":0}}}""",
        "unknown value type 'decimal'",
        TestName = "ExecuteRowWithAnUnknownValueDiscriminator")]
    [TestCase(
        "execute",
        """{"type":"response_ok","request_id":ID,"response":{"type":"execute","result":{"cols":[{"name":"a"}],"rows":[[{"type":"integer","value":"not-a-number"}]],"affected_row_count":0}}}""",
        "signed 64-bit integer",
        TestName = "ExecuteRowWithAnOutOfRangeInteger")]
    [TestCase(
        "execute",
        """{"type":"response_error","request_id":ID}""",
        "mandatory 'error' object",
        TestName = "ResponseErrorWithoutAnErrorObject")]
    [TestCase(
        "execute",
        """{"type":"response_error","request_id":ID,"error":{"code":"X"}}""",
        "mandatory string 'message'",
        TestName = "ResponseErrorWithoutAMessage")]
    [TestCase(
        "execute",
        """{"type":"response_ok","request_id":ID,"response":{"type":"close_stream"}}""",
        "answered a 'execute' request with a 'close_stream' response",
        TestName = "ResponseTypeMismatch")]
    public async Task MalformedResponsesFaultTheGenerationInsteadOfBecomingDefaults(
        string requestType,
        string rawResponse,
        string expectedFragment)
    {
        var server = new FakeHranaServer();
        server.RawResponses[requestType] = rawResponse;
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            Func<Task> request = requestType == "get_autocommit"
                ? async () => await transport.GetAutocommitAsync(commandTimeout: 30, CancellationToken.None)
                : async () => await transport.ExecuteAsync(
                    new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                    commandTimeout: 30,
                    closeAfter: false,
                    CancellationToken.None);

            (await request.Should().ThrowAsync<AhtolaException>()).WithMessage($"*{expectedFragment}*");
            await WaitForAsync(() => server.Socket!.Aborted);
            server.Socket!.Aborted.Should().BeTrue("a broken response contract must terminate the generation");
        }
    }

    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[]}}""",
        "'done'",
        TestName = "FetchCursorWithoutDone")]
    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","done":true}}""",
        "'entries'",
        TestName = "FetchCursorWithoutEntries")]
    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[{"type":"step_skipped","step":0}],"done":true}}""",
        "unknown cursor entry type 'step_skipped'",
        TestName = "FetchCursorWithAnUnknownEntryDiscriminator")]
    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[{"type":"step_begin","cols":[]}],"done":true}}""",
        "numeric 'step'",
        TestName = "StepBeginWithoutAStep")]
    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[{"type":"step_begin","step":0,"cols":[{"name":"a"}]},{"type":"step_end"}],"done":true}}""",
        "numeric 'affected_row_count'",
        TestName = "StepEndWithoutAnAffectedRowCount")]
    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[{"type":"step_begin","step":0,"cols":[{"name":"a"}]},{"type":"row","row":[{"type":"geometry"}]}],"done":true}}""",
        "unknown value type 'geometry'",
        TestName = "CursorRowWithAnUnknownValueDiscriminator")]
    [TestCase(
        """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[{"type":"error"}],"done":true}}""",
        "mandatory 'error' object",
        TestName = "CursorErrorWithoutAnErrorObject")]
    public async Task MalformedCursorPagesFaultTheGeneration(string rawResponse, string expectedFragment)
    {
        var server = new FakeHranaServer();
        server.RawResponses["fetch_cursor"] = rawResponse;
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            var batch = new RemoteBatch
            {
                Steps = [new RemoteBatchStep { Statement = new RemoteStatement { Sql = "SELECT 1", WantRows = true } }],
            };
            var session = await transport.OpenCursorAsync(batch, commandTimeout: 30, CancellationToken.None);
            session.Should().NotBeNull();

            var fetch = async () => await session!.FetchAsync(128, commandTimeout: 30, CancellationToken.None);

            (await fetch.Should().ThrowAsync<AhtolaException>()).WithMessage($"*{expectedFragment}*");
            await WaitForAsync(() => server.Socket!.Aborted);
        }
    }

    [Test]
    public void AnUnknownEntryTypeIsNeverSilentlySkippedByTheCursorReader()
    {
        // Even if a future entry type slipped past the contract check, the reader itself must
        // refuse it: skipping could silently truncate a result set.
        var server = new FakeHranaServer();
        server.RawResponses["fetch_cursor"] =
            """{"type":"response_ok","request_id":ID,"response":{"type":"fetch_cursor","entries":[{"type":"step_progress","step":0}],"done":true}}""";
        AhtolaConnection.RemoteWebSocketConnectorFactory = () => server;
        try
        {
            using var connection = new AhtolaConnection("Data Source=wss://database.example/db");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM t";

            var read = () =>
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                }
            };

            read.Should().Throw<AhtolaException>().WithMessage("*step_progress*");
        }
        finally
        {
            AhtolaConnection.RemoteWebSocketConnectorFactory = null;
        }
    }

    // ---------------------------------------------------------------------------------
    // 7. closeAfter cleanup still runs for application errors.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task CloseAfterStillClosesTheStreamWhenTheServerReturnsAnApplicationError()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["execute"] = ("SQLITE_ERROR", "no such table: t");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            var execute = async () => await transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT * FROM t", WantRows = true },
                commandTimeout: 30,
                closeAfter: true,
                CancellationToken.None);

            (await execute.Should().ThrowAsync<AhtolaException>()).WithMessage("*no such table: t*");

            server.RequestTypes.Should().Equal("open_stream", "execute", "close_stream");
            server.ClosedStreamIds.Should().Equal(server.OpenedStreamIds);
            transport.HasOpenSession.Should().BeFalse();

            // A rejected statement is an ordinary application error: the generation lives on.
            server.RequestErrors.Clear();
            var next = await transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                commandTimeout: 30,
                closeAfter: true,
                CancellationToken.None);
            next.Rows.Should().ContainSingle();
            server.ConnectAttempts.Should().Be(1, "an application error must not force a reconnect");
        }
    }

    [Test]
    public async Task CloseAfterStillClosesTheStreamWhenABatchStepIsRejected()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["batch"] = ("SQLITE_ERROR", "constraint failed");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            var batch = new RemoteBatch
            {
                Steps = [new RemoteBatchStep { Statement = new RemoteStatement { Sql = "INSERT INTO t VALUES (1)" } }],
            };
            var run = async () => await transport.ExecuteBatchAsync(batch, commandTimeout: 30, closeAfter: true, CancellationToken.None);

            (await run.Should().ThrowAsync<AhtolaException>()).WithMessage("*constraint failed*");
            server.ClosedStreamIds.Should().Equal(server.OpenedStreamIds);
            transport.HasOpenSession.Should().BeFalse();
        }
    }

    [Test]
    public async Task CloseAfterIsSkippedWhenTheGenerationIsAlreadyDead()
    {
        var server = new FakeHranaServer { CloseOnRequestType = "execute" };
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            var execute = async () => await transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                commandTimeout: 30,
                closeAfter: true,
                CancellationToken.None);

            await execute.Should().ThrowAsync<AhtolaException>();

            // The stream died with the generation, so there is nothing left to close.
            server.RequestTypes.Should().NotContain("close_stream");
        }
    }

    // ---------------------------------------------------------------------------------
    // 8. Version gating happens before a stream is opened.
    // ---------------------------------------------------------------------------------

    [TestCase("hrana1", "sequence")]
    [TestCase("hrana1", "describe")]
    [TestCase("hrana1", "get_autocommit")]
    [TestCase("hrana2", "get_autocommit")]
    public async Task VersionGatingRunsBeforeAStreamIsOpened(string subProtocol, string requestType)
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = subProtocol };
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            Func<Task> request = requestType switch
            {
                "sequence" => async () => await transport.RunSequenceAsync(
                    "SELECT 1; SELECT 2;", sqlId: null, commandTimeout: 30, closeAfter: true, CancellationToken.None),
                "describe" => async () => await transport.DescribeAsync(
                    "SELECT 1", sqlId: null, commandTimeout: 30, closeAfter: true, CancellationToken.None),
                _ => async () => await transport.GetAutocommitAsync(commandTimeout: 30, CancellationToken.None),
            };

            (await request.Should().ThrowAsync<AhtolaException>()).WithMessage("*does not support*");

            server.RequestTypes.Should().NotContain("open_stream", "no stream may be minted for a request that cannot run");
            server.OpenedStreamIds.Should().BeEmpty();
            transport.HasOpenSession.Should().BeFalse();
        }

        // Nothing had to be compensated, so no close_stream was needed either.
        server.RequestTypes.Should().NotContain("close_stream");
    }

    [Test]
    public async Task VersionGatingLeavesAnExistingSessionIntact()
    {
        var server = new FakeHranaServer { NegotiatedSubProtocol = "hrana2" };
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            await transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                commandTimeout: 30,
                closeAfter: false,
                CancellationToken.None);
            transport.HasOpenSession.Should().BeTrue();

            var gated = async () => await transport.GetAutocommitAsync(commandTimeout: 30, CancellationToken.None);
            (await gated.Should().ThrowAsync<AhtolaException>()).WithMessage("*does not support*");

            transport.HasOpenSession.Should().BeTrue("a local gating failure must not close the caller's session");
            server.OpenedStreamIds.Should().ContainSingle();
        }
    }

    // ---------------------------------------------------------------------------------
    // 9. Half-open detection without ping frames.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task TheWatchdogAbortsAHalfOpenGenerationWithOutstandingRequests()
    {
        var server = new FakeHranaServer();
        server.DropRequestTypes.Add("execute");
        var connection = await ConnectAsync(
            server,
            FastOptions(halfOpenTimeout: TimeSpan.FromMilliseconds(200)));
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(streamId), TimeSpan.Zero, CancellationToken.None);

            var stalled = async () => await connection.SendRequestAsync(
                HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                TimeSpan.Zero,
                CancellationToken.None);

            (await stalled.Should().ThrowAsync<AhtolaException>()).WithMessage("*half-open*");
            connection.IsAlive.Should().BeFalse();
            server.Socket!.Aborted.Should().BeTrue();
            server.ReceivedMessages.Should().NotContain(message => message.Contains("ping", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Test]
    public async Task TheWatchdogIsOffUnlessAHalfOpenTimeoutIsConfigured()
    {
        // Keep-alives alone must not arm it. A Hrana server sends nothing while a statement
        // runs, so silence + outstanding requests is exactly what a slow query looks like;
        // deriving the budget from the keep-alive settings would kill healthy connections.
        var server = new FakeHranaServer();
        server.DropRequestTypes.Add("execute");
        var connection = await ConnectAsync(
            server,
            FastOptions(
                keepAliveInterval: TimeSpan.FromMilliseconds(50),
                keepAliveTimeout: TimeSpan.FromMilliseconds(50)));
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(streamId), TimeSpan.Zero, CancellationToken.None);

            var stalled = connection.SendRequestAsync(
                HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                TimeSpan.Zero,
                CancellationToken.None);

            await Task.Delay(600);

            stalled.IsCompleted.Should().BeFalse("liveness must not be promised from keep-alive settings alone");
            connection.IsAlive.Should().BeTrue();
        }
    }

    [Test]
    public async Task ASlowButHealthyStatementIsNotTreatedAsHalfOpen()
    {
        // The server answers, just slowly. With a budget above the reply latency the
        // watchdog must stay quiet — this is the regression that separates "busy" from
        // "dead" for a protocol that sends nothing in between.
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        var connection = await ConnectAsync(
            server,
            FastOptions(halfOpenTimeout: TimeSpan.FromMilliseconds(1500)));
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(streamId), TimeSpan.Zero, CancellationToken.None);

            var slow = connection.SendRequestAsync(
                HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                TimeSpan.Zero,
                CancellationToken.None);

            await WaitForAsync(() => server.HeldRequestCount == 1);
            await Task.Delay(500);
            slow.IsCompleted.Should().BeFalse();
            connection.IsAlive.Should().BeTrue();

            server.ReleaseHeldRequests();
            (await slow).Type.Should().Be("execute");
            connection.IsAlive.Should().BeTrue();
        }
    }

    [Test]
    public async Task TheWatchdogLeavesAnIdleConnectionAlone()
    {
        var server = new FakeHranaServer();
        var connection = await ConnectAsync(
            server,
            FastOptions(halfOpenTimeout: TimeSpan.FromMilliseconds(100)));
        await using (connection)
        {
            // Nothing is outstanding, so a silent peer is not evidence of a half-open socket.
            await Task.Delay(500);

            connection.IsAlive.Should().BeTrue();
            var response = await connection.SendRequestAsync(
                HranaRequest.ForOpenStream(connection.AllocateStreamId()),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            response.Type.Should().Be("open_stream");
        }
    }

    [Test]
    public async Task TrafficKeepsTheWatchdogQuiet()
    {
        var server = new FakeHranaServer();
        var connection = await ConnectAsync(
            server,
            FastOptions(halfOpenTimeout: TimeSpan.FromMilliseconds(160)));
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(HranaRequest.ForOpenStream(streamId), TimeSpan.FromSeconds(10), CancellationToken.None);

            var deadline = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < deadline)
            {
                await connection.SendRequestAsync(
                    HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
                await Task.Delay(20);
            }

            connection.IsAlive.Should().BeTrue("a busy connection is never half-open");
        }
    }

    [Test]
    public async Task AnIdlePeriodDoesNotCountAgainstTheNextRequest()
    {
        // Regression: the watchdog once measured silence only since the last inbound frame,
        // so after an idle stretch the very next request was aborted on its first tick —
        // even though it had barely been outstanding.
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("execute");
        var connection = await ConnectAsync(
            server,
            FastOptions(halfOpenTimeout: TimeSpan.FromMilliseconds(600)));
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(
                HranaRequest.ForOpenStream(streamId),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            // Idle far longer than the budget, so "silence since the last frame" is stale.
            await Task.Delay(900);

            var request = connection.SendRequestAsync(
                HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            // Keep it outstanding well past a watchdog tick, but inside the budget.
            await WaitForAsync(() => server.HeldRequestCount == 1);
            await Task.Delay(250);
            server.ReleaseHeldRequests();

            (await request).Type.Should().Be("execute");
            connection.IsAlive.Should().BeTrue();
        }
    }

    [Test]
    public void ANegativeHalfOpenTimeoutIsRejected()
    {
        var invalid = () => new AhtolaHranaWebSocketOptions { HalfOpenTimeout = TimeSpan.FromSeconds(-1) }.Validate();

        invalid.Should().Throw<InvalidOperationException>().WithMessage("*Ws Half Open Timeout*");
    }

    [Test]
    public void TheHalfOpenTimeoutRoundTripsThroughTheConnectionString()
    {
        var options = AhtolaConnectionOptions
            .Parse("Data Source=wss://database.example;Ws Half Open Timeout=45")
            .GetWebSocketOptions();

        options.HalfOpenTimeout.Should().Be(TimeSpan.FromSeconds(45));
        AhtolaConnectionOptions
            .Parse("Data Source=wss://database.example")
            .GetWebSocketOptions()
            .HalfOpenTimeout.Should().Be(TimeSpan.Zero, "liveness checking is opt-in");
    }

    // ---------------------------------------------------------------------------------
    // A request that never reached the wire must not pin a correlation slot.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task ALifecycleRequestThatNeverReachedTheWireReleasesItsSlot()
    {
        // The send queue is full and the caller is cancelled while waiting for capacity, so
        // the frame is never enqueued. No server handle can exist, so the slot must be
        // tombstoned and released rather than held open waiting for an impossible reply.
        var server = new FakeHranaServer { BlockSendsAfter = 1 };
        var connection = await ConnectAsync(server, FastOptions(sendQueueCapacity: 1));
        await using (connection)
        {
            // Wedge the send loop, then fill the single queue slot.
            var wedged = connection.SendRequestAsync(
                HranaRequest.ForOpenStream(connection.AllocateStreamId()),
                TimeSpan.Zero,
                CancellationToken.None);
            await WaitForAsync(() => server.Socket!.SendCallCount >= 2);

            var queued = connection.SendRequestAsync(
                HranaRequest.ForOpenStream(connection.AllocateStreamId()),
                TimeSpan.Zero,
                CancellationToken.None);
            await Task.Delay(50);

            using var cancellation = new CancellationTokenSource();
            var blocked = connection.SendRequestAsync(
                HranaRequest.ForOpenStream(connection.AllocateStreamId()),
                TimeSpan.Zero,
                cancellation.Token,
                orphanCompensation: () => Task.CompletedTask);
            await Task.Delay(50);

            await cancellation.CancelAsync();
            var cancelled = async () => await blocked;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();

            // Two slots stay open (the wedged send and the queued frame); the third, which
            // never made it into the queue, must have been released.
            await WaitForAsync(() => connection.OutstandingRequestCount <= 2);
            connection.OutstandingRequestCount.Should().BeLessThanOrEqualTo(2);
            connection.CancelledRequestCount.Should().Be(1);

            _ = wedged.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            _ = queued.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }
    }

    [Test]
    public async Task AnAbandonedStoreSqlReleasesTheStoredSqlWhenTheServerAnswersLate()
    {
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("store_sql");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            using var cancellation = new CancellationTokenSource();
            var store = transport.StoreSqlAsync("SELECT :id", commandTimeout: 30, cancellation.Token);

            await WaitForAsync(() => server.HeldRequestCount == 1);
            await cancellation.CancelAsync();
            var cancelled = async () => await store;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();

            server.ReleaseHeldRequests();
            await WaitForAsync(() => server.RequestTypes.Contains("close_sql"));
        }
    }

    // ---------------------------------------------------------------------------------
    // 10. Dispose / DisposeAsync converge on one idempotent disposal.
    // ---------------------------------------------------------------------------------

    [Test]
    public void SynchronousDisposeDoesNotReturnUntilTheSocketIsDisposed()
    {
        var server = new FakeHranaServer { BlockSendsAfter = 1 };
        var transport = new AhtolaHranaWebSocketTransport(
            Endpoint,
            authToken: null,
            FastOptions(closeTimeout: TimeSpan.FromMilliseconds(150)),
            server);

        // Command timeout releases the transport gate so disposal can proceed, but the send
        // loop itself stays wedged inside SendAsync until the socket is aborted.
        var wedged = Task.Run(async () =>
        {
            try
            {
                await transport.ExecuteAsync(
                    new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                    commandTimeout: 1,
                    closeAfter: true,
                    CancellationToken.None);
            }
            catch
            {
                // The wedged send is expected to fail.
            }
        });

        WaitForAsync(() => server.Socket is { SendCallCount: >= 2 }).GetAwaiter().GetResult();

        var stopwatch = Stopwatch.StartNew();
        transport.Dispose();
        stopwatch.Stop();

        server.Socket!.Disposed.Should().BeTrue("Dispose() must not return while the socket is still live");
        server.Socket.Aborted.Should().BeTrue();
        server.Socket.SawCloseOutputDuringSend.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        wedged.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
    }

    [Test]
    public async Task DisposeAndDisposeAsyncConvergeOnOneIdempotentDisposal()
    {
        var server = new FakeHranaServer();
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1", WantRows = true },
            commandTimeout: 30,
            closeAfter: false,
            CancellationToken.None);

        // Both entry points, twice each, concurrently: exactly one close_stream must be sent.
        await Task.WhenAll(
            Task.Run(() => transport.Dispose()),
            Task.Run(async () => await transport.DisposeAsync()),
            Task.Run(() => transport.Dispose()),
            Task.Run(async () => await transport.DisposeAsync()));

        server.RequestTypes.Count(type => type == "close_stream").Should().Be(1);
        server.Socket!.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task ConnectionDisposalIsIdempotentAcrossBothEntryPoints()
    {
        var server = new FakeHranaServer();
        var connection = await ConnectAsync(server);

        await connection.DisposeAsync();
        await connection.DisposeAsync();
        connection.Dispose();

        server.Socket!.Disposed.Should().BeTrue();
        connection.IsAlive.Should().BeFalse();
    }

    [Test]
    public async Task AHealthyDisposalStillSendsTheCloseFrame()
    {
        var server = new FakeHranaServer();
        var connection = await ConnectAsync(server);

        await connection.DisposeAsync();

        server.Socket!.CloseOutputSent.Should().BeTrue("a live send loop must still perform the courtesy close");
        server.Socket.SentCloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        server.Socket.SawCloseOutputDuringSend.Should().BeFalse();
        server.Socket.Disposed.Should().BeTrue();
    }

    [Test]
    public void SynchronousDisposeDoesNotStallBehindAnOperationHoldingTheLifecycleGate()
    {
        // The open_stream is held forever with no command timeout, so the acquire gate stays
        // taken. Disposal must still complete: it gives up the courtesy close and aborts.
        var server = new FakeHranaServer();
        server.HoldRequestTypes.Add("open_stream");
        var transport = new AhtolaHranaWebSocketTransport(
            Endpoint,
            authToken: null,
            FastOptions(closeTimeout: TimeSpan.FromMilliseconds(150)),
            server);

        var stalled = Task.Run(async () =>
        {
            try
            {
                await transport.ExecuteAsync(
                    new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                    commandTimeout: 0,
                    closeAfter: true,
                    CancellationToken.None);
            }
            catch
            {
                // Expected: the generation is torn down underneath it.
            }
        });

        WaitForAsync(() => server.HeldRequestCount == 1).GetAwaiter().GetResult();

        var stopwatch = Stopwatch.StartNew();
        transport.Dispose();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        server.Socket!.Disposed.Should().BeTrue();
        server.Socket.Aborted.Should().BeTrue();
        stalled.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
    }

    [Test]
    public async Task AFailedTrailingCloseIsToleratedAndNeverRepeated()
    {
        // The trailing close itself is rejected. Mirroring the HTTP pipeline, that does not
        // fail the caller's statement — and the failure path must not queue a second
        // close_stream for a stream the transport no longer owns.
        var server = new FakeHranaServer();
        server.RequestErrors["close_stream"] = ("INTERNAL", "already gone");
        var transport = new AhtolaHranaWebSocketTransport(Endpoint, authToken: null, FastOptions(), server);
        await using (transport)
        {
            var result = await transport.ExecuteAsync(
                new RemoteStatement { Sql = "SELECT 1", WantRows = true },
                commandTimeout: 30,
                closeAfter: true,
                CancellationToken.None);

            result.Rows.Should().ContainSingle();
            server.RequestTypes.Count(type => type == "close_stream").Should().Be(1);
            transport.HasOpenSession.Should().BeFalse();
        }
    }

    [Test]
    public async Task ConcurrentCancellationsAndRepliesKeepTheGenerationHealthy()
    {
        // A stress mix: many callers race between abandoning their wait and the server
        // answering, so both the tombstone path and the "response beat the cancellation"
        // path are exercised repeatedly on one generation.
        var server = new FakeHranaServer();
        var connection = await ConnectAsync(server);
        await using (connection)
        {
            var streamId = connection.AllocateStreamId();
            await connection.SendRequestAsync(
                HranaRequest.ForOpenStream(streamId),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

            var workers = new List<Task>();
            for (var worker = 0; worker < 8; worker++)
            {
                workers.Add(Task.Run(async () =>
                {
                    for (var index = 0; index < 40; index++)
                    {
                        using var cancellation = new CancellationTokenSource();
                        var request = connection.SendRequestAsync(
                            HranaRequest.ForExecute(streamId, new RemoteStatement { Sql = "SELECT 1", WantRows = true }),
                            TimeSpan.Zero,
                            cancellation.Token);

                        // Cancel at an unpredictable point relative to the reply.
                        if (index % 3 == 0)
                            await cancellation.CancelAsync();

                        try
                        {
                            await request;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                }));
            }

            await Task.WhenAll(workers);
            await Task.Delay(150);

            connection.IsAlive.Should().BeTrue();
            connection.Fault.Should().BeNull();
            server.Socket!.SawConcurrentSend.Should().BeFalse();
            server.Socket.SawConcurrentReceive.Should().BeFalse();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 10000)
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
