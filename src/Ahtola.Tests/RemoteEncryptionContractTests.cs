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
    public void RemoteConnectionsAcceptEncryptionSettings()
    {
        using var connection = new AhtolaConnection(
            "Data Source=https://example.com;Encryption Cipher=Aes256Gcm;Encryption Key=c2VjcmV0");

        connection.Open();

        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Test]
    public void RemoteClientSendsEncryptionKeyHeader()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: null,
            remoteEncryption: new AhtolaRemoteEncryptionOptions(
                "c2VjcmV0",
                AhtolaRemoteEncryptionCipher.Aes256Gcm));

        Assert.ThrowsAsync<AhtolaException>(() => client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None));

        handler.EncryptionKey.Should().Be("c2VjcmV0");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? EncryptionKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            EncryptionKey = request.Headers.TryGetValues("x-turso-encryption-key", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("rejected", Encoding.UTF8, "text/plain"),
            });
        }
    }
}
