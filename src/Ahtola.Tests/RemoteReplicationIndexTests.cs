using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class RemoteReplicationIndexTests
{
    [Test]
    public async Task RemoteBatchCarriesTheHighestObservedReplicationIndex()
    {
        using var handler = new ReplicationIndexHandler(
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0,"replication_index":"42"}],"step_errors":[null],"replication_index":"41"}}}]}
            """,
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0,"replication_index":"7"}],"step_errors":[null],"replication_index":"6"}}}]}
            """);
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: null);
        var commands = new[] { new AhtolaBatchCommand("SELECT 1") };

        await client.ExecuteBatchAsync(
            commands,
            commandTimeout: 30,
            wantRows: true,
            closeAfter: true,
            CancellationToken.None);
        await client.ExecuteBatchAsync(
            commands,
            commandTimeout: 30,
            wantRows: true,
            closeAfter: true,
            CancellationToken.None);

        handler.RequestReplicationIndexes.Should().Equal(null, "42");
    }

    [TestCase("\"not-an-index\"")]
    [TestCase("1")]
    public void RemoteBatchRejectsAnInvalidOrUnencodedReplicationIndex(string encodedIndex)
    {
        using var handler = new ReplicationIndexHandler(
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null],"replication_index":"__INDEX__"}}}]}
            """.Replace("\"__INDEX__\"", encodedIndex, StringComparison.Ordinal));
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: null);

        Assert.ThrowsAsync<AhtolaException>(() => client.ExecuteBatchAsync(
            [new AhtolaBatchCommand("SELECT 1")],
            commandTimeout: 30,
            wantRows: true,
            closeAfter: true,
            CancellationToken.None))!
            .Message.Should().Be("Remote response returned an invalid replication_index.");
    }

    private sealed class ReplicationIndexHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string?> RequestReplicationIndexes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var batch = document.RootElement
                .GetProperty("requests")[0]
                .GetProperty("batch");
            RequestReplicationIndexes.Add(
                batch.TryGetProperty("replication_index", out var replicationIndex)
                    ? replicationIndex.GetString()
                    : null);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
