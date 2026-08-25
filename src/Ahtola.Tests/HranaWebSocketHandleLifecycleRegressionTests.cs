using System.Net.WebSockets;

using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for the Hrana WebSocket handle lifecycle: a reserved <c>stream_id</c>/<c>cursor_id</c>
/// is released even when the request that reserved it was rejected, an unconfirmed close never
/// silently forgets a live server-side handle, and a socket that finishes connecting after disposal
/// started can never be published.
/// </summary>
public sealed class HranaWebSocketHandleLifecycleRegressionTests
{
    private static AhtolaHranaWebSocketOptions Options(TimeSpan? closeTimeout = null)
        => new()
        {
            ConnectAttempts = 1,
            ConnectRetryDelay = TimeSpan.Zero,
            CloseTimeout = closeTimeout ?? TimeSpan.FromSeconds(5),
            KeepAliveInterval = TimeSpan.Zero,
        };

    private static AhtolaHranaWebSocketTransport CreateTransport(FakeHranaServer server)
        => new(new Uri("wss://database.example"), authToken: null, Options(), server);

    [Test]
    public async Task ARejectedOpenStreamStillReleasesTheReservedStreamId()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["open_stream"] = ("SQLITE_BUSY", "too many streams");
        await using var transport = CreateTransport(server);

        var execute = async () => await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None);

        await execute.Should().ThrowAsync<AhtolaException>();

        // A response_error answers the request but does not prove the server minted no stream, and
        // the id is not reusable either way. The compensating close is what makes the handle
        // provably gone; without it a rejected open leaks a stream for the life of the connection.
        server.RequestTypes.Should().Contain("close_stream");
        server.ClosedStreamIds.Should().Contain(server.OpenedStreamIds.DefaultIfEmpty(1).Last());
    }

    [Test]
    public async Task ARejectedOpenCursorStillReleasesTheReservedCursorId()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["open_cursor"] = ("SQLITE_ERROR", "cursor rejected");
        await using var transport = CreateTransport(server);

        var open = async () => await transport.OpenCursorAsync(
            new RemoteBatch { Steps = [new RemoteBatchStep { Statement = new RemoteStatement { Sql = "SELECT 1" } }] },
            commandTimeout: 5,
            CancellationToken.None);

        await open.Should().ThrowAsync<AhtolaException>();

        server.RequestTypes.Should().Contain("close_cursor");
    }

    [Test]
    public async Task ARejectedCompensationRetiresTheGenerationRatherThanForgettingTheHandle()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["open_stream"] = ("SQLITE_BUSY", "too many streams");

        // The compensating close is never answered, so the client cannot prove the handle is gone.
        server.DropRequestTypes.Add("close_stream");
        await using var transport = new AhtolaHranaWebSocketTransport(
            new Uri("wss://database.example"),
            authToken: null,
            Options(TimeSpan.FromMilliseconds(200)),
            server);

        var execute = async () => await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None);

        await execute.Should().ThrowAsync<AhtolaException>();

        // Ending the generation is the only remaining way to make the server reclaim the handle.
        transport.NegotiatedVersion.Should().BeNull("an unprovable handle retires the generation");
    }

    [Test]
    public async Task AFailedTrailingCloseStreamRetiresTheGenerationInsteadOfLeakingTheStream()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["close_stream"] = ("SQLITE_ERROR", "close refused");
        await using var transport = CreateTransport(server);

        await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: true,
            CancellationToken.None);

        // The transport dropped its own reference to the stream before issuing the close, so a
        // swallowed failure would forget a stream the server may still hold.
        transport.HasOpenSession.Should().BeFalse();
        transport.NegotiatedVersion.Should().BeNull("an unconfirmed close retires the generation");
    }

    [Test]
    public async Task AFailedCloseSessionRetiresTheGenerationAndSurfacesTheFailure()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["close_stream"] = ("SQLITE_ERROR", "close refused");
        await using var transport = CreateTransport(server);

        await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None);
        transport.HasOpenSession.Should().BeTrue();

        var close = async () => await transport.CloseSessionAsync(5, CancellationToken.None);
        await close.Should().ThrowAsync<AhtolaException>();

        transport.HasOpenSession.Should().BeFalse();
        transport.NegotiatedVersion.Should().BeNull();
    }

    [Test]
    public async Task AFailedCloseCursorRetiresTheGenerationInsteadOfLeakingTheCursor()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["close_cursor"] = ("SQLITE_ERROR", "close refused");
        await using var transport = CreateTransport(server);

        var cursor = await transport.OpenCursorAsync(
            new RemoteBatch { Steps = [new RemoteBatchStep { Statement = new RemoteStatement { Sql = "SELECT 1" } }] },
            commandTimeout: 5,
            CancellationToken.None);
        cursor.Should().NotBeNull();

        await cursor!.CloseAsync(5);

        transport.NegotiatedVersion.Should().BeNull("an unconfirmed cursor close retires the generation");
    }

    [Test]
    public async Task ASocketThatConnectsAfterDisposalIsNeverPublished()
    {
        // Disposal only waits CloseTimeout for the lifecycle gate, so a connect still in flight can
        // outlive it. Publishing that socket afterwards would leak it and its two loops.
        var gate = new SemaphoreSlim(0, 1);
        var server = new BlockingConnectFakeHranaServer(gate);
        var transport = new AhtolaHranaWebSocketTransport(
            new Uri("wss://database.example"),
            authToken: null,
            Options(TimeSpan.FromMilliseconds(50)),
            server);

        var pending = Task.Run(async () => await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None));

        await server.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = transport.DisposeAsync().AsTask();
        await Task.Delay(150);
        gate.Release();

        await disposal.WaitAsync(TimeSpan.FromSeconds(30));
        var outcome = async () => await pending;
        await outcome.Should().ThrowAsync<Exception>();

        foreach (var socket in server.Sockets)
            socket.Disposed.Should().BeTrue("a socket refused publication must be disposed, not leaked");
    }

    /// <summary>A fake server whose connect handshake blocks until a gate is released.</summary>
    private sealed class BlockingConnectFakeHranaServer(SemaphoreSlim gate) : IAhtolaWebSocketConnector
    {
        private readonly FakeHranaServer _inner = new();

        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<FakeWebSocket> Sockets => _inner.Sockets;

        public async Task<WebSocket> ConnectAsync(
            Uri endpoint,
            IReadOnlyList<string> subProtocols,
            AhtolaHranaWebSocketOptions options,
            CancellationToken cancellationToken)
        {
            ConnectStarted.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.ConnectAsync(endpoint, subProtocols, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
