using System.Diagnostics;

using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regressions for two Hrana WebSocket paths that used to forget a live server-side handle
/// instead of retiring the generation: <see cref="AhtolaHranaWebSocketTransport.ResetSession"/>'s
/// fire-and-forget <c>close_stream</c>, and <see cref="AhtolaHranaWebSocketTransport.CloseSqlAsync"/>'s
/// <c>close_sql</c>. Both must honour the same policy as every other handle close in this
/// transport: an unconfirmed close (error, drop, or timeout) retires the whole generation, because
/// dropping the socket is the only remaining way to make the server reclaim the handle.
/// </summary>
public sealed class HranaWebSocketResetAndStoredSqlRegressionTests
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

    // ---------------------------------------------------------------------------------
    // ResetSession(): the fire-and-forget close_stream must not forget a live stream.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task AResetSessionCloseErrorRetiresTheGenerationRatherThanForgettingTheStream()
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

        transport.ResetSession();
        await transport.ResetSessionCompletion;

        // The transport already dropped its own reference to the stream before asking the
        // server to close it, so a swallowed error would forget a stream the server may
        // still hold on a generation that stays alive and keeps being reused.
        transport.NegotiatedVersion.Should().BeNull("an unconfirmed close retires the generation");
        transport.HasOpenSession.Should().BeFalse();
    }

    [Test]
    public async Task AResetSessionCloseThatIsNeverAnsweredRetiresTheGenerationRatherThanForgettingTheStream()
    {
        var server = new FakeHranaServer();
        server.DropRequestTypes.Add("close_stream");
        await using var transport = new AhtolaHranaWebSocketTransport(
            new Uri("wss://database.example"),
            authToken: null,
            Options(TimeSpan.FromMilliseconds(200)),
            server);

        await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None);
        transport.HasOpenSession.Should().BeTrue();

        transport.ResetSession();
        await transport.ResetSessionCompletion;

        transport.NegotiatedVersion.Should().BeNull("an unconfirmed close retires the generation");
        transport.HasOpenSession.Should().BeFalse();
    }

    [Test]
    public async Task AResetSessionWithAConfirmedCloseLeavesTheGenerationAlive()
    {
        var server = new FakeHranaServer();
        await using var transport = CreateTransport(server);

        await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None);
        transport.HasOpenSession.Should().BeTrue();

        transport.ResetSession();
        await transport.ResetSessionCompletion;

        // A confirmed close proves the server released the stream: the fix must not make
        // every reset destroy the connection, only an unconfirmed one.
        transport.NegotiatedVersion.Should().NotBeNull("a confirmed close must not retire a healthy generation");
        transport.HasOpenSession.Should().BeFalse();
        server.RequestTypes.Should().Contain("close_stream");
    }

    [Test]
    public async Task ResetSessionReturnsBeforeAnUnansweredCloseCompletes()
    {
        var server = new FakeHranaServer();
        server.DropRequestTypes.Add("close_stream");
        await using var transport = new AhtolaHranaWebSocketTransport(
            new Uri("wss://database.example"),
            authToken: null,
            Options(TimeSpan.FromSeconds(2)),
            server);

        await transport.ExecuteAsync(
            new RemoteStatement { Sql = "SELECT 1" },
            commandTimeout: 5,
            closeAfter: false,
            CancellationToken.None);
        transport.HasOpenSession.Should().BeTrue();

        var stopwatch = Stopwatch.StartNew();
        transport.ResetSession();
        stopwatch.Stop();

        // The close is never going to be answered, and CloseTimeout gives it two full
        // seconds to try. The public seam must not wait around for either.
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(1),
            "ResetSession must be a non-blocking fire-and-forget seam, not a wrapper that awaits the close");

        await transport.ResetSessionCompletion;
        transport.NegotiatedVersion.Should().BeNull("an unconfirmed close retires the generation");
        transport.HasOpenSession.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------
    // CloseSqlAsync(): a lost close_sql response must not forget a live stored-SQL handle.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task AFailedCloseSqlRetiresTheGenerationInsteadOfLeakingTheStoredSql()
    {
        var server = new FakeHranaServer();
        server.RequestErrors["close_sql"] = ("SQLITE_ERROR", "close refused");
        await using var transport = CreateTransport(server);

        var sqlId = await transport.StoreSqlAsync("SELECT 1", commandTimeout: 5, CancellationToken.None);

        var close = async () => await transport.CloseSqlAsync(sqlId, commandTimeout: 5, CancellationToken.None);
        await close.Should().ThrowAsync<AhtolaException>();

        // The caller has already stopped tracking sqlId; only retiring the generation can
        // still guarantee the server reclaims the stored SQL text.
        transport.NegotiatedVersion.Should().BeNull("an unconfirmed close retires the generation");
    }

    [Test]
    public async Task AnUnansweredCloseSqlRetiresTheGenerationInsteadOfLeakingTheStoredSql()
    {
        var server = new FakeHranaServer();
        server.DropRequestTypes.Add("close_sql");
        await using var transport = CreateTransport(server);

        var sqlId = await transport.StoreSqlAsync("SELECT 1", commandTimeout: 5, CancellationToken.None);

        // commandTimeout is whole seconds and CloseSqlAsync has no sub-second timeout knob,
        // so 1 second is the shortest wait that still proves the point.
        var close = async () => await transport.CloseSqlAsync(sqlId, commandTimeout: 1, CancellationToken.None);
        await close.Should().ThrowAsync<OperationCanceledException>();

        transport.NegotiatedVersion.Should().BeNull("an unconfirmed close retires the generation");
    }

    [Test]
    public async Task ACloseSqlThatSucceedsLeavesTheGenerationAlive()
    {
        var server = new FakeHranaServer();
        await using var transport = CreateTransport(server);

        var sqlId = await transport.StoreSqlAsync("SELECT 1", commandTimeout: 5, CancellationToken.None);
        await transport.CloseSqlAsync(sqlId, commandTimeout: 5, CancellationToken.None);

        // The fix must not be over-eager: a confirmed close is not an orphaned handle.
        transport.NegotiatedVersion.Should().NotBeNull("a confirmed close must not retire a healthy generation");
        server.RequestTypes.Should().Contain("close_sql");
    }
}
