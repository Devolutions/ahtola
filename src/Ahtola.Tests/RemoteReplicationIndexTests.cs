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

    // turso-src/serverless/PROTOCOL.md (v0.8.1) section 7.2.6: clients MUST NOT interpret the
    // replication_index value. An uninterpretable value is therefore ignored -- the request
    // succeeds and the previously tracked watermark is kept -- instead of failing the batch.
    [TestCase("\"not-an-index\"")]
    [TestCase("\"-1\"")]
    [TestCase("1.5")]
    [TestCase("-3")]
    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("true")]
    public async Task RemoteBatchIgnoresAnUninterpretableReplicationIndex(string encodedIndex)
    {
        using var handler = new ReplicationIndexHandler(
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null],"replication_index":"5"}}}]}
            """,
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0,"replication_index":__INDEX__}],"step_errors":[null],"replication_index":__INDEX__}}}]}
            """.Replace("__INDEX__", encodedIndex, StringComparison.Ordinal),
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null]}}}]}
            """);
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: null);
        var commands = new[] { new AhtolaBatchCommand("SELECT 1") };

        for (var request = 0; request < 3; request++)
            await client.ExecuteBatchAsync(commands, 30, wantRows: true, closeAfter: true, CancellationToken.None);

        handler.RequestReplicationIndexes.Should().Equal(null, "5", "5");
    }

    [Test]
    public async Task RemoteBatchAcceptsLegacyNumericReplicationIndex()
    {
        using var handler = new ReplicationIndexHandler(
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null],"replication_index":42}}}]}
            """,
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":0}],"step_errors":[null]}}}]}
            """);
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.com"),
            authToken: null);
        var commands = new[] { new AhtolaBatchCommand("SELECT 1") };

        await client.ExecuteBatchAsync(commands, 30, wantRows: true, closeAfter: true, CancellationToken.None);
        await client.ExecuteBatchAsync(commands, 30, wantRows: true, closeAfter: true, CancellationToken.None);

        handler.RequestReplicationIndexes.Should().Equal(null, "42");
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
