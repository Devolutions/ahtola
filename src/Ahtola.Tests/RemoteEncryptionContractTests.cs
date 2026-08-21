using System.Net;
using System.Net.Http;
using System.Text;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class RemoteEncryptionContractTests
{
    [Test]
    public void PartialBootstrapAndRemoteEncryptionAreMutuallyExclusive()
    {
        var options = new AhtolaReplicaOptions(
            "replica.db",
            new Uri("https://example.com"),
            authToken: null)
        {
            PartialBootstrap = AhtolaPartialBootstrapOptions.Prefix(4096),
            RemoteEncryption = new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm),
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate())!
            .Message.Should().Be("Partial bootstrap cannot be combined with remote encryption.");
    }

    [Test]
    public void ReplicaOptionsRejectEncryptionWhenInjectedTransportMayAutomaticallyRedirect()
    {
        using var handler = new CapturingHandler();
        var options = new AhtolaReplicaOptions(
            "replica.db",
            new Uri("https://example.com"),
            authToken: null)
        {
            HttpPolicy = new AhtolaSyncHttpPolicy(handler),
            RemoteEncryption = new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm),
        };

        var action = () => options.Validate();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Remote encryption requires an HTTP transport that guarantees automatic redirects are disabled.");
        handler.CallCount.Should().Be(0);
    }

    [Test]
    public void RemoteConnectionsAcceptEncryptionSettings()
    {
        using var connection = new AhtolaConnection(
            "Data Source=https://example.com;Encryption Cipher=Aes256Gcm;Encryption Key=c2VjcmV0");

        connection.Open();

        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Test]
    public void RemoteClientRejectsEncryptionOverNonLoopbackHttpWithoutAuthToken()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);

        var action = () => new AhtolaRemoteClient(
            httpClient,
            new Uri("http://database.example"),
            authToken: null,
            remoteEncryption: new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm),
            automaticRedirectsDisabled: true);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Remote encryption requires an HTTPS remote Ahtola URL unless the host is localhost or loopback.");
        handler.CallCount.Should().Be(0);
    }

    [TestCase("https://example.com")]
    [TestCase("http://localhost")]
    public void RemoteClientAllowsEncryptionOverHttpsOrLoopbackHttp(string endpoint)
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri(endpoint),
            authToken: null,
            remoteEncryption: new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm),
            automaticRedirectsDisabled: true);

        Assert.ThrowsAsync<AhtolaException>(() => client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None));

        handler.EncryptionKey.Should().Be("c2VjcmV0");
        handler.CallCount.Should().Be(1);
    }

    [Test]
    public void RemoteClientRejectsEncryptionWhenInjectedTransportMayAutomaticallyRedirect()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);

        var action = () => new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: null,
            remoteEncryption: new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Remote encryption requires an HTTP transport that guarantees automatic redirects are disabled.");
        handler.CallCount.Should().Be(0);
    }

    [Test]
    public async Task RemoteClientBlocksLoopbackRedirectToNonLoopbackHttpBeforeLeakingEncryptionKey()
    {
        using var handler = new RedirectingHandler(
            new Uri("http://database.example/v2/pipeline"),
            HttpStatusCode.TemporaryRedirect);
        using var httpClient = new HttpClient(handler);
        using var client = CreateEncryptedRemoteClient(
            httpClient,
            new Uri("http://localhost"));

        Func<Task> action = () => ExecuteAsync(client);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "Remote encryption requires an HTTPS remote Ahtola URL unless the host is localhost or loopback.");
        handler.RequestUris.Should().ContainSingle();
        handler.RequestUris[0].Host.Should().Be("localhost");
        handler.EncryptionKeys.Should().ContainSingle().Which.Should().Be("c2VjcmV0");
    }

    [TestCase("http://localhost", "http://localhost/redirected")]
    [TestCase("https://origin.example", "https://origin.example/redirected")]
    public async Task RemoteClientFollowsAllowedRedirectsWithoutChangingPostBody(
        string endpoint,
        string destination)
    {
        using var handler = new RedirectingHandler(
            new Uri(destination),
            HttpStatusCode.TemporaryRedirect);
        using var httpClient = new HttpClient(handler);
        using var client = CreateEncryptedRemoteClient(httpClient, new Uri(endpoint));

        Func<Task> action = () => ExecuteAsync(client);

        await action.Should().ThrowAsync<AhtolaException>()
            .WithMessage("Remote request failed with HTTP 500*");
        handler.RequestUris.Should().HaveCount(2);
        handler.RequestUris[1].Should().Be(new Uri(destination));
        handler.Methods.Should().OnlyContain(method => method == HttpMethod.Post);
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[1].Should().Be(handler.Bodies[0]);
        handler.EncryptionKeys.Should().OnlyContain(key => key == "c2VjcmV0");
    }

    [Test]
    public async Task RemoteClientBlocksCrossOriginHttpsRedirectBeforeLeakingEncryptionKey()
    {
        using var handler = new RedirectingHandler(
            new Uri("https://destination.example/v2/pipeline"),
            HttpStatusCode.TemporaryRedirect);
        using var httpClient = new HttpClient(handler);
        using var client = CreateEncryptedRemoteClient(
            httpClient,
            new Uri("https://origin.example"));

        Func<Task> action = () => ExecuteAsync(client);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "Credential-bearing remote Ahtola requests cannot redirect to a different origin.");
        handler.RequestUris.Should().ContainSingle();
        handler.RequestUris[0].Host.Should().Be("origin.example");
        handler.EncryptionKeys.Should().ContainSingle().Which.Should().Be("c2VjcmV0");
    }

    [Test]
    public async Task RemoteClientRejectsRedirectsThatWouldChangePostSemantics()
    {
        using var handler = new RedirectingHandler(
            new Uri("https://destination.example/v2/pipeline"),
            HttpStatusCode.Redirect);
        using var httpClient = new HttpClient(handler);
        using var client = CreateEncryptedRemoteClient(
            httpClient,
            new Uri("https://origin.example"));

        Func<Task> action = () => ExecuteAsync(client);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "Remote Ahtola requests follow only HTTP 307 and 308 redirects so the request method and body are preserved.");
        handler.RequestUris.Should().ContainSingle();
    }

    private static AhtolaRemoteClient CreateEncryptedRemoteClient(HttpClient httpClient, Uri endpoint)
        => new(
            httpClient,
            endpoint,
            authToken: null,
            remoteEncryption: new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm),
            automaticRedirectsDisabled: true);

    private static Task<RemoteStatementResult> ExecuteAsync(AhtolaRemoteClient client)
        => client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? EncryptionKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            EncryptionKey = request.Headers.TryGetValues("x-turso-encryption-key", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("rejected", Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class RedirectingHandler(Uri destination, HttpStatusCode redirectStatus)
        : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string?> EncryptionKeys { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            Methods.Add(request.Method);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            EncryptionKeys.Add(
                request.Headers.TryGetValues("x-turso-encryption-key", out var values)
                    ? values.Single()
                    : null);

            if (RequestUris.Count == 1)
            {
                return new HttpResponseMessage(redirectStatus)
                {
                    Headers = { Location = destination },
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("rejected", Encoding.UTF8, "text/plain"),
            };
        }
    }
}
