using System.Net;
using System.Text;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Pins the embedded-replica connection-string keywords Ahtola accepts for parity with Turso's
/// <c>TursoConnectionStringBuilder</c> (<c>turso-src/bindings/dotnet/src/Turso.Data</c>), their
/// mapping onto <see cref="AhtolaReplicaOptions"/>, and the per-request auth-token provider.
/// </summary>
public sealed class ReplicaConnectionStringOptionsTests
{
    private const string AdvancedReplicaConnectionString =
        "Data Source=libsql://db.example.test;Auth Token=secret;Replica Path=replica.db;"
        + "SyncClientName=my-app;Sync Long Poll Timeout=1500;BootstrapIfEmpty=False;"
        + "Partial Bootstrap Prefix=8192;Partial Sync Segment Size=16384;PartialSyncPrefetch=True;"
        + "Push Operations Threshold=10;Sync Interval=5;AutomaticSyncMode=PullOnly;"
        + "Remote Encryption Cipher=aes256gcm;Remote Encryption Key=c2VjcmV0;"
        + "Force Logical MVCC Pull=False;Sync Experimental Features=views, strict";

    [Test]
    public void AhtolaBuilderAcceptsTursoReplicaKeywordsAndAliases()
    {
        var builder = new AhtolaConnectionStringBuilder(AdvancedReplicaConnectionString);

        builder.SyncClientName.Should().Be("my-app");
        builder.SyncLongPollTimeout.Should().Be(1500);
        builder.BootstrapIfEmpty.Should().BeFalse();
        builder.PartialBootstrapPrefix.Should().Be(8192);
        builder.PartialBootstrapQuery.Should().BeEmpty();
        builder.PartialSyncSegmentSize.Should().Be(16384);
        builder.PartialSyncPrefetch.Should().BeTrue();
        builder.PushOperationsThreshold.Should().Be(10);
        builder.PullBytesThreshold.Should().Be(0);
        builder.RemoteEncryptionCipher.Should().Be("aes256gcm");
        builder.RemoteEncryptionKey.Should().Be("c2VjcmV0");
        builder.ForceLogicalMvccPull.Should().BeFalse();
        builder.SyncExperimentalFeatures.Should().Be("views, strict");
        builder.AutomaticSyncMode.Should().Be(AhtolaAutomaticSyncMode.PullOnly);

        var defaults = new AhtolaConnectionStringBuilder();
        defaults.BootstrapIfEmpty.Should().BeTrue();
        defaults.AutomaticSyncMode.Should().Be(AhtolaAutomaticSyncMode.PushAndPull);
        defaults.PullBytesThreshold = 4096;
        defaults.ConnectionString.Should().Be("Pull Bytes Threshold=4096");
    }

    [Test]
    public void ConnectionStringKeywordsMapOntoReplicaOptions()
    {
        var options = AhtolaConnectionOptions.Parse(AdvancedReplicaConnectionString)
            .CreateReplicaOptions(messageHandler: null, authTokenProvider: null);

        options.Path.Should().Be("replica.db");
        options.RemoteUri.Should().Be(new Uri("https://db.example.test/"));
        options.AuthToken.Should().Be("secret");
        options.ClientName.Should().Be("my-app");
        options.LongPollTimeout.Should().Be(TimeSpan.FromMilliseconds(1500));
        options.BootstrapIfEmpty.Should().BeFalse();
        options.PartialBootstrap.Should().NotBeNull();
        options.PartialBootstrap!.Kind.Should().Be(AhtolaPartialBootstrapKind.Prefix);
        options.PartialBootstrap.PrefixLength.Should().Be(8192);
        options.PartialBootstrap.SegmentSize.Should().Be(16384);
        options.PartialBootstrap.Prefetch.Should().BeTrue();
        options.PushOperationsThreshold.Should().Be(10);
        options.PullBytesThreshold.Should().BeNull();
        options.SyncInterval.Should().Be(5);
        options.AutomaticSyncMode.Should().Be(AhtolaAutomaticSyncMode.PullOnly);
        options.RemoteEncryption.Should().NotBeNull();
        options.RemoteEncryption!.Cipher.Should().Be(AhtolaRemoteEncryptionCipher.Aes256Gcm);
        options.RemoteEncryption.Base64Key.Should().Be("c2VjcmV0");

        var query = AhtolaConnectionOptions.Parse(
                "Data Source=https://db.example.test;Replica Path=replica.db;Partial Bootstrap Query=SELECT * FROM t;Pull Bytes Threshold=65536")
            .CreateReplicaOptions(null, null);
        query.PartialBootstrap!.Kind.Should().Be(AhtolaPartialBootstrapKind.Query);
        query.PartialBootstrap.Query.Should().Be("SELECT * FROM t");
        query.PullBytesThreshold.Should().Be(65536);
        query.BootstrapIfEmpty.Should().BeTrue();
        query.LongPollTimeout.Should().BeNull();
        query.ClientName.Should().BeNull();
        query.AutomaticSyncMode.Should().Be(AhtolaAutomaticSyncMode.PushAndPull);
    }

    [TestCase("aes128gcm", AhtolaRemoteEncryptionCipher.Aes128Gcm)]
    [TestCase("AES-256-GCM", AhtolaRemoteEncryptionCipher.Aes256Gcm)]
    [TestCase("chacha20poly1305", AhtolaRemoteEncryptionCipher.ChaCha20Poly1305)]
    [TestCase("aegis128l", AhtolaRemoteEncryptionCipher.Aegis128L)]
    [TestCase("aegis_256x4", AhtolaRemoteEncryptionCipher.Aegis256X4)]
    public void RemoteEncryptionCipherNamesFollowTurso(string name, AhtolaRemoteEncryptionCipher expected)
        => AhtolaRemoteEncryptionOptions.ParseCipher(name).Should().Be(expected);

    [TestCase("Partial Bootstrap Prefix=4096;Partial Bootstrap Query=SELECT 1", "cannot be combined")]
    [TestCase("Partial Sync Segment Size=4096", "require Partial Bootstrap Prefix")]
    [TestCase("Partial Sync Prefetch=True", "require Partial Bootstrap Prefix")]
    [TestCase("Remote Encryption Cipher=aes256gcm", "must be specified together")]
    [TestCase("Remote Encryption Key=c2VjcmV0", "must be specified together")]
    [TestCase("Remote Encryption Cipher=rot13;Remote Encryption Key=c2VjcmV0", "Unknown remote encryption cipher")]
    public void InvalidReplicaKeywordCombinationsFailClosed(string keywords, string message)
    {
        var options = AhtolaConnectionOptions.Parse(
            "Data Source=https://db.example.test;Replica Path=replica.db;" + keywords);

        Assert.Throws<InvalidOperationException>(() => options.CreateReplicaOptions(null, null))!
            .Message.Should().Contain(message);
    }

    [Test]
    public void UnsupportedTursoEscapeHatchesFailClosedOrValidate()
    {
        var forced = AhtolaConnectionOptions.Parse(
            "Data Source=https://db.example.test;Replica Path=replica.db;Force Logical MVCC Pull=True");
        Assert.Throws<NotSupportedException>(() => forced.CreateReplicaOptions(null, null))!
            .Message.Should().Contain("Force Logical MVCC Pull");

        var features = AhtolaConnectionOptions.Parse(
            "Data Source=https://db.example.test;Replica Path=replica.db;Sync Experimental Features='views,,x'");
        Assert.Throws<ArgumentException>(() => features.CreateReplicaOptions(null, null));
    }

    [Test]
    public void AdvancedReplicaKeywordsRequireAnEmbeddedReplica()
    {
        using var local = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed;Sync Client Name=x");
        Assert.Throws<InvalidOperationException>(() => local.Open())!
            .Message.Should().Be("Advanced sync options require a remote embedded replica connection.");

        using var remote = new AhtolaConnection("Data Source=https://db.example.test;Automatic Sync Mode=PullOnly");
        Assert.Throws<NotSupportedException>(() => remote.Open())!
            .Message.Should().Be("Advanced sync options require an embedded replica connection.");

        using var tokenOnLocal = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed")
        {
            AuthTokenProvider = _ => ValueTask.FromResult<string?>("t"),
        };
        Assert.Throws<InvalidOperationException>(() => tokenOnLocal.Open());
    }

    [Test]
    public void SqliteFacadeForwardsReplicaKeywordsToTheAhtolaConnectionString()
    {
        var facade = new Ahtola.Data.Sqlite.SqliteConnectionStringBuilder(AdvancedReplicaConnectionString);
        facade.SyncClientName.Should().Be("my-app");
        facade.PartialSyncSegmentSize.Should().Be(16384);
        facade.AutomaticSyncMode.Should().Be(AhtolaAutomaticSyncMode.PullOnly);
        facade.Keys.Cast<string>().Should().Contain(
            ["Sync Client Name", "Remote Encryption Key", "Force Logical MVCC Pull", "Automatic Sync Mode"]);
        facade["Pull Bytes Threshold"].Should().Be(0L);

        var forwarded = new AhtolaConnectionStringBuilder(facade.GetAhtolaConnectionString());
        forwarded.SyncClientName.Should().Be("my-app");
        forwarded.SyncLongPollTimeout.Should().Be(1500);
        forwarded.BootstrapIfEmpty.Should().BeFalse();
        forwarded.PartialBootstrapPrefix.Should().Be(8192);
        forwarded.PartialSyncSegmentSize.Should().Be(16384);
        forwarded.PartialSyncPrefetch.Should().BeTrue();
        forwarded.PushOperationsThreshold.Should().Be(10);
        forwarded.RemoteEncryptionCipher.Should().Be("aes256gcm");
        forwarded.RemoteEncryptionKey.Should().Be("c2VjcmV0");
        forwarded.SyncExperimentalFeatures.Should().Be("views, strict");
        forwarded.AutomaticSyncMode.Should().Be(AhtolaAutomaticSyncMode.PullOnly);
        forwarded.LocalProvider.Should().Be(AhtolaLocalProvider.Managed);
    }

    [Test]
    public async Task RemoteClientResolvesTheTokenProviderForEveryRequest()
    {
        using var handler = new AuthorizationRecordingHandler();
        using var httpClient = new HttpClient(handler);
        var issued = 0;
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: "static-token",
            authTokenProvider: _ => ValueTask.FromResult<string?>(
                ++issued == 2 ? " " : "token-" + issued.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var commands = new[] { new AhtolaBatchCommand("SELECT 1") };

        for (var request = 0; request < 3; request++)
            await client.ExecuteBatchAsync(commands, 30, wantRows: true, closeAfter: true, CancellationToken.None);

        handler.Authorizations.Should().Equal("Bearer token-1", null, "Bearer token-3");
    }

    [Test]
    public void TokenProviderStillRequiresHttpsForNonLoopbackHosts()
    {
        using var httpClient = new HttpClient(new AuthorizationRecordingHandler());
        Assert.Throws<InvalidOperationException>(() => _ = new AhtolaRemoteClient(
            httpClient,
            new Uri("http://example.com"),
            authToken: null,
            authTokenProvider: _ => ValueTask.FromResult<string?>("token")));

        Assert.Throws<InvalidOperationException>(() => AhtolaConnection.CreateReplica(
            new AhtolaReplicaOptions("replica.db", new Uri("http://example.com"), authToken: null)
            {
                AuthTokenProvider = _ => ValueTask.FromResult<string?>("token"),
            }));
    }

    [Test]
    public async Task WebSocketHelloUsesTheTokenProviderOnEveryConnect()
    {
        var server = new FakeHranaServer();
        var issued = 0;
        using var client = new AhtolaRemoteClient(
            new Uri("wss://database.example"),
            authToken: "static-token",
            AhtolaHranaWebSocketOptions.Default,
            remoteEncryption: null,
            server,
            _ => ValueTask.FromResult<string?>(
                "ws-token-" + Interlocked.Increment(ref issued).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        _ = await HranaWebSocketTransportTests.ExecuteAsync(client, "SELECT 1");

        server.ObservedJwt.Should().Be("ws-token-1");
    }

    private sealed class AuthorizationRecordingHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null]}}}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
