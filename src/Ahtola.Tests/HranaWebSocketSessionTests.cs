using System.Globalization;
using System.Text.Json;

using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Transport selection, session lifecycle, reconnect and security coverage for the Hrana
/// WebSocket transport, including proof that the HTTP pipeline path is unchanged.
/// </summary>
public sealed class HranaWebSocketSessionTests
{
    private Func<IAhtolaWebSocketConnector?>? _priorConnectorFactory;
    private Func<HttpMessageHandler?>? _priorHandlerFactory;

    [SetUp]
    public void CaptureFactories()
    {
        _priorConnectorFactory = AhtolaConnection.RemoteWebSocketConnectorFactory;
        _priorHandlerFactory = AhtolaConnection.RemoteMessageHandlerFactory;
    }

    [TearDown]
    public void RestoreFactories()
    {
        AhtolaConnection.RemoteWebSocketConnectorFactory = _priorConnectorFactory;
        AhtolaConnection.RemoteMessageHandlerFactory = _priorHandlerFactory;
    }

    [TestCase("ws://localhost:8080")]
    [TestCase("wss://database.example")]
    public void WebSocketSchemesSelectTheWebSocketTransport(string dataSource)
    {
        var options = AhtolaConnectionOptions.Parse($"Data Source={dataSource}");

        options.IsWebSocketRemote.Should().BeTrue();
        options.GetRemoteWebSocketUri().Scheme.Should().Be(new Uri(dataSource).Scheme);
    }

    [TestCase("https://database.example")]
    [TestCase("http://localhost:8080")]
    [TestCase("libsql://database.example")]
    [TestCase("turso://database.example")]
    [TestCase("C:/tmp/local.db")]
    public void NonWebSocketSchemesKeepTheHttpPipeline(string dataSource)
    {
        var options = AhtolaConnectionOptions.Parse($"Data Source={dataSource}");

        options.IsWebSocketRemote.Should().BeFalse();
    }

    [Test]
    public void ReplicaConnectionsNeverSelectTheWebSocketTransport()
    {
        var options = AhtolaConnectionOptions.Parse(
            "Data Source=wss://database.example;Replica Path=C:/tmp/replica.db");

        options.IsWebSocketRemote.Should().BeFalse("embedded replicas keep the HTTP sync pipeline");
    }

    [Test]
    public async Task TheUpgradeUsesTheUrlPathVerbatimWithoutAPipelineSuffix()
    {
        var server = new FakeHranaServer();
        using var client = HranaWebSocketTransportTests.CreateClient(
            server,
            endpoint: new Uri("wss://database.example/tenant/db"));

        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 1");

        server.ConnectedEndpoints.Should().ContainSingle();
        server.ConnectedEndpoints[0].Should().Be(new Uri("wss://database.example/tenant/db"));
        server.ConnectedEndpoints[0].AbsolutePath.Should().NotContain("/v3").And.NotContain("/v2");
    }

    [Test]
    public async Task RootUrlsUpgradeOnTheRootPath()
    {
        var server = new FakeHranaServer();
        using var client = HranaWebSocketTransportTests.CreateClient(server, endpoint: new Uri("wss://database.example"));

        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 1");

        server.ConnectedEndpoints[0].AbsolutePath.Should().Be("/");
    }

    [Test]
    public void AuthTokensRequireWssOrLoopback()
    {
        var server = new FakeHranaServer();

        var insecure = () => HranaWebSocketTransportTests.CreateClient(
            server,
            authToken: "secret",
            endpoint: new Uri("ws://database.example"));
        insecure.Should().Throw<InvalidOperationException>().WithMessage("*Auth Token requires*");

        var loopback = () => HranaWebSocketTransportTests.CreateClient(
            server,
            authToken: "secret",
            endpoint: new Uri("ws://localhost:9000"));
        loopback.Should().NotThrow();

        var secure = () => HranaWebSocketTransportTests.CreateClient(
            server,
            authToken: "secret",
            endpoint: new Uri("wss://database.example"));
        secure.Should().NotThrow();
    }

    [Test]
    public void RemoteEncryptionOverWebSocketsFailsClosed()
    {
        var server = new FakeHranaServer();

        var create = () => new AhtolaRemoteClient(
            new Uri("wss://database.example"),
            authToken: null,
            AhtolaHranaWebSocketOptions.Default,
            new AhtolaRemoteEncryptionOptions(
                Convert.ToBase64String(new byte[32]),
                AhtolaRemoteEncryptionCipher.Aes256Gcm),
            server);

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("*hello message has no encryption-key field*");
        server.ConnectAttempts.Should().Be(0);
    }

    [Test]
    public void ANonWebSocketUrlIsRejectedByTheWebSocketClient()
    {
        var server = new FakeHranaServer();

        var create = () => HranaWebSocketTransportTests.CreateClient(server, endpoint: new Uri("https://database.example"));

        create.Should().Throw<InvalidOperationException>().WithMessage("*requires a ws or wss URL*");
    }

    [Test]
    public async Task AGenerationLossWithAnOpenSessionInvalidatesTheSessionAndReplaysNothing()
    {
        var server = new FakeHranaServer();
        using var client = HranaWebSocketTransportTests.CreateClient(server);

        // Open a session (no closeAfter) and start a transaction-shaped write.
        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "BEGIN", closeAfter: false);
        client.HasOpenSession.Should().BeTrue();

        server.CloseOnRequestType = "execute";
        var write = async () => await HranaWebSocketTransportTests.ExecuteAsync(
            client,
            "INSERT INTO t VALUES (1)",
            closeAfter: false);
        (await write.Should().ThrowAsync<AhtolaException>()).WithMessage("*closed by the server*");

        // The next operation must fail closed instead of silently starting a new stream.
        server.CloseOnRequestType = null;
        var next = async () => await HranaWebSocketTransportTests.ExecuteAsync(client, "COMMIT", closeAfter: false);
        (await next.Should().ThrowAsync<AhtolaException>()).WithMessage("*invalidated*");

        server.ExecutedSql.Should().ContainSingle().Which.Should().Be("BEGIN", "the failed write must never be replayed");
        server.ConnectAttempts.Should().Be(1);
    }

    [Test]
    public async Task ResetSessionAllowsAFreshGenerationWithAFreshStream()
    {
        var server = new FakeHranaServer();
        using var client = HranaWebSocketTransportTests.CreateClient(server);
        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "BEGIN", closeAfter: false);

        server.CloseOnRequestType = "execute";
        var write = async () => await HranaWebSocketTransportTests.ExecuteAsync(client, "INSERT INTO t VALUES (1)", closeAfter: false);
        await write.Should().ThrowAsync<AhtolaException>();
        server.CloseOnRequestType = null;

        // ResetSession() is a fire-and-forget seam over an async close: await the completion
        // seam so the reset (clearing the invalidation reason and dropping the dead stream) is
        // guaranteed to have run before the next operation is attempted.
        client.ResetSession();
        await client.WebSocketResetCompletion;
        var recovered = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 1");

        recovered.Rows.Should().ContainSingle();
        server.ConnectAttempts.Should().Be(2, "a new generation is a new connection, never a resumed one");
        server.OpenedStreamIds.Should().HaveCount(2);
    }

    [Test]
    public async Task AnIdleGenerationLossIsReplacedTransparentlyForTheNextStatelessOperation()
    {
        var server = new FakeHranaServer();
        using var client = HranaWebSocketTransportTests.CreateClient(server);
        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 1");
        client.HasOpenSession.Should().BeFalse();

        // Kill the idle socket the way a proxy would.
        server.Socket!.PushClose();
        await HranaWebSocketTransportTests.WaitForAsync(() => server.Socket!.Aborted);

        var recovered = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 2");

        recovered.Rows.Should().ContainSingle();
        server.ConnectAttempts.Should().Be(2);
        server.ExecutedSql.Should().Equal("SELECT 1", "SELECT 2");
    }

    [Test]
    public async Task CursorFailureInvalidatesTheSessionWithoutReplay()
    {
        var server = new FakeHranaServer { CursorPageLimit = 1 };
        server.Rows.Clear();
        server.Rows.AddRange(["1", "2", "3"]);
        using var client = HranaWebSocketTransportTests.CreateClient(server);

        var execution = await client.ExecuteCursorAsync(
            "SELECT value FROM t",
            new AhtolaParameterCollection(),
            commandTimeout: 30,
            closeAfter: false,
            CancellationToken.None);
        var cursor = execution.Cursor!;
        (await cursor.ReadRowAsync(CancellationToken.None)).Should().NotBeNull();

        server.CloseOnRequestType = "fetch_cursor";
        var read = async () => await cursor.ReadRowAsync(CancellationToken.None);
        await read.Should().ThrowAsync<AhtolaException>();
        server.CloseOnRequestType = null;

        var next = async () => await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 9", closeAfter: false);
        (await next.Should().ThrowAsync<AhtolaException>()).WithMessage("*invalidated*");
        server.ExecutedSql.Should().NotContain("SELECT 9");
    }

    [Test]
    public async Task ClosingTheSessionSendsCloseStreamAndClearsTheSession()
    {
        var server = new FakeHranaServer();
        using var client = HranaWebSocketTransportTests.CreateClient(server);
        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 1", closeAfter: false);

        await client.CloseAsync(30, CancellationToken.None);

        client.HasOpenSession.Should().BeFalse();
        server.ClosedStreamIds.Should().ContainSingle();
    }

    [Test]
    public async Task AdoConnectionsOverWssExecuteReadersEndToEnd()
    {
        var server = new FakeHranaServer();
        server.Rows.Clear();
        server.Rows.AddRange(["10", "20"]);
        AhtolaConnection.RemoteWebSocketConnectorFactory = () => server;

        using var connection = new AhtolaConnection("Data Source=wss://database.example/db;Auth Token=token");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM t";
        var values = new List<long>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                values.Add(reader.GetInt64(0));
        }

        values.Should().Equal(10, 20);
        server.ConnectedEndpoints.Should().ContainSingle();
        server.ConnectedEndpoints[0].Scheme.Should().Be("wss");
        server.ObservedJwt.Should().Be("token");
    }

    [Test]
    public void HttpDataSourcesNeverTouchTheWebSocketConnector()
    {
        var server = new FakeHranaServer();
        using var handler = new SingleExecuteHandler();
        AhtolaConnection.RemoteWebSocketConnectorFactory = () => server;
        AhtolaConnection.RemoteMessageHandlerFactory = () => handler;

        using var connection = new AhtolaConnection("Data Source=https://database.example;Auth Token=token");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.ExecuteScalar().Should().Be(1L);

        server.ConnectAttempts.Should().Be(0, "http/https must keep using the HTTP pipeline");
        handler.Paths.Should().NotBeEmpty();
        handler.Paths[0].Should().EndWith("/pipeline");
    }

    [Test]
    public async Task DisposingTheAdoConnectionClosesTheStreamAndTheSocket()
    {
        var server = new FakeHranaServer();
        AhtolaConnection.RemoteWebSocketConnectorFactory = () => server;

        using (var connection = new AhtolaConnection("Data Source=wss://database.example;Read Your Writes=True"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = command.ExecuteScalar();
        }

        await HranaWebSocketTransportTests.WaitForAsync(() => server.Socket!.CloseOutputSent || server.Socket.Aborted);
        server.Socket!.State.Should().NotBe(System.Net.WebSockets.WebSocketState.Open);
    }

    [Test]
    public void SqliteFacadeConnectionsUseTheWebSocketTransportForWssDataSources()
    {
        var server = new FakeHranaServer();
        server.Rows.Clear();
        server.Rows.Add("42");
        var priorFactory = Ahtola.Data.Sqlite.SqliteConnection.RemoteWebSocketConnectorFactory;
        Ahtola.Data.Sqlite.SqliteConnection.RemoteWebSocketConnectorFactory = () => server;
        try
        {
            using var connection = new Ahtola.Data.Sqlite.SqliteConnection(
                "Data Source=wss://database.example/db;Auth Token=token;Read Your Writes=True");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM t";

            command.ExecuteScalar().Should().Be(42L);
            server.ConnectedEndpoints.Should().ContainSingle();
            server.ConnectedEndpoints[0].Scheme.Should().Be("wss");
        }
        finally
        {
            Ahtola.Data.Sqlite.SqliteConnection.RemoteWebSocketConnectorFactory = priorFactory;
        }
    }

    [Test]
    public void SqliteFacadeBuilderRoundTripsTheWebSocketKnobsIntoTheAhtolaConnectionString()
    {
        var builder = new Ahtola.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = "wss://database.example",
            WsKeepaliveInterval = 12,
            WsKeepaliveTimeout = 7,
            WsMaxMessageBytes = 65536,
            WsConnectAttempts = 2,
        };

        builder.WsKeepaliveInterval.Should().Be(12);
        builder.WsKeepaliveTimeout.Should().Be(7);
        builder.WsMaxMessageBytes.Should().Be(65536);
        builder.WsConnectAttempts.Should().Be(2);

        // Aliases resolve to the same canonical keyword on the facade too.
        builder["WebSocketConnectAttempts"] = 5;
        builder.WsConnectAttempts.Should().Be(5);

        var options = AhtolaConnectionOptions.Parse(builder.ConnectionString).GetWebSocketOptions();
        options.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(12));
        options.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(7));
        options.MaxMessageBytes.Should().Be(65536);
        options.ConnectAttempts.Should().Be(5);
    }

    [Test]
    public void SqliteFacadeBuilderTreatsWsHalfOpenTimeoutAsAFirstClassKeyword()
    {
        var builder = new Ahtola.Data.Sqlite.SqliteConnectionStringBuilder();

        // The keyword was reachable through the property and the alias map but was missing from the
        // canonical list, the default table and the integer conversion table, so enumerating it or
        // reading it before it was set threw instead of answering the documented default.
        builder.Keys.Cast<string>().Should().Contain("Ws Half Open Timeout");
        builder.TryGetValue("Ws Half Open Timeout", out var unset).Should().BeTrue();
        unset.Should().Be(0);
        builder["Ws Half Open Timeout"].Should().Be(0);
        builder.Values.Cast<object?>().Should().Contain(0);

        // Every alias converts to the canonical keyword rather than throwing.
        builder["WebSocketHalfOpenTimeout"] = "45";
        builder.WsHalfOpenTimeout.Should().Be(45);
        Convert.ToInt32(builder["Ws Half Open Timeout"], CultureInfo.InvariantCulture).Should().Be(45);

        builder.DataSource = "wss://database.example";
        var options = AhtolaConnectionOptions
            .Parse(builder.GetAhtolaConnectionString())
            .GetWebSocketOptions();
        options.HalfOpenTimeout.Should().Be(TimeSpan.FromSeconds(45));

        var roundTripped = new Ahtola.Data.Sqlite.SqliteConnectionStringBuilder(builder.ConnectionString);
        roundTripped.WsHalfOpenTimeout.Should().Be(45);

        var invalid = () => builder.WsHalfOpenTimeout = -1;
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SqliteFacadeBuilderRejectsInvalidWebSocketKnobs()
    {
        var builder = new Ahtola.Data.Sqlite.SqliteConnectionStringBuilder();

        var tooSmall = () => builder.WsMaxMessageBytes = 1024;
        tooSmall.Should().Throw<ArgumentOutOfRangeException>();
        var tooMany = () => builder.WsConnectAttempts = 25;
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
        var negative = () => builder.WsKeepaliveInterval = -5;
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void ConnectionStringKnobsParseThroughEveryAlias()
    {
        var options = AhtolaConnectionOptions.Parse(
            "Data Source=wss://database.example;WsKeepaliveInterval=5;WebSocket Keepalive Timeout=6;"
            + "WsMaxMessageBytes=65536;WebSocketConnectAttempts=4");

        var webSocketOptions = options.GetWebSocketOptions();

        webSocketOptions.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(5));
        webSocketOptions.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(6));
        webSocketOptions.MaxMessageBytes.Should().Be(65536);
        webSocketOptions.ConnectAttempts.Should().Be(4);
    }

    [Test]
    public void ConnectionStringKnobsUseSpecDefaultsWhenAbsent()
    {
        var options = AhtolaConnectionOptions.Parse("Data Source=wss://database.example").GetWebSocketOptions();

        options.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxMessageBytes.Should().Be(16 * 1024 * 1024);
        options.ConnectAttempts.Should().Be(3);
    }

    [TestCase("Ws Max Message Bytes=64", "Ws Max Message Bytes must be between*")]
    [TestCase("Ws Connect Attempts=0", "Ws Connect Attempts must be between*")]
    [TestCase("Ws Connect Attempts=99", "Ws Connect Attempts must be between*")]
    [TestCase("Ws Keepalive Interval=-1", "Ws Keepalive Interval must be*")]
    [TestCase("Ws Max Message Bytes=abc", "Ws Max Message Bytes must be an integer*")]
    public void InvalidConnectionStringKnobsAreRejected(string keyword, string message)
    {
        var options = AhtolaConnectionOptions.Parse($"Data Source=wss://database.example;{keyword}");

        var read = () => options.GetWebSocketOptions();

        read.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Test]
    public void BuilderPropertiesRoundTripTheWebSocketKnobs()
    {
        var builder = new AhtolaConnectionStringBuilder
        {
            DataSource = "wss://database.example",
            WsKeepaliveInterval = 15,
            WsKeepaliveTimeout = 4,
            WsMaxMessageBytes = 32 * 1024,
            WsConnectAttempts = 2,
        };

        builder.WsKeepaliveInterval.Should().Be(15);
        builder.WsKeepaliveTimeout.Should().Be(4);
        builder.WsMaxMessageBytes.Should().Be(32 * 1024);
        builder.WsConnectAttempts.Should().Be(2);
        builder.ConnectionString.Should().Contain("Ws Keepalive Interval=15");

        var tooSmall = () => builder.WsMaxMessageBytes = 16;
        tooSmall.Should().Throw<ArgumentOutOfRangeException>();
        var tooMany = () => builder.WsConnectAttempts = 50;
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void TlsConflictsAreRejectedForWebSocketUrls()
    {
        var secureWithTlsOff = () => AhtolaConnectionOptions
            .Parse("Data Source=wss://database.example;Tls=False")
            .GetRemoteWebSocketUri();
        secureWithTlsOff.Should().Throw<InvalidOperationException>().WithMessage("*conflicts with the wss URL scheme*");

        var insecureWithTlsOn = () => AhtolaConnectionOptions
            .Parse("Data Source=ws://database.example;Tls=True")
            .GetRemoteWebSocketUri();
        insecureWithTlsOn.Should().Throw<InvalidOperationException>().WithMessage("*conflicts with the ws URL scheme*");
    }

    [TestCase("wss://database.example?x=1", "*query strings or fragments*")]
    [TestCase("wss://user:pass@database.example", "*user information*")]
    public void MalformedWebSocketUrlsAreRejected(string dataSource, string message)
    {
        var read = () => AhtolaConnectionOptions.Parse($"Data Source={dataSource}").GetRemoteWebSocketUri();

        read.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    /// <summary>Minimal HTTP pipeline handler used to prove the HTTP path is untouched.</summary>
    private sealed class SingleExecuteHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            const string body = """
                {"baton":null,"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"value"}],"rows":[[{"type":"integer","value":"1"}]],"affected_row_count":0}}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
