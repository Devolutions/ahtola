using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class SourceGeneratedRemoteJsonTests
{
    [Test]
    public async Task RemotePipelineSerializesAndDeserializesUsingGeneratedMetadata()
    {
        using var handler = new GeneratedJsonHandler();
        using var client = new AhtolaRemoteClient(
            new HttpClient(handler),
            new Uri("https://example.test"),
            authToken: null);

        var result = await client.ExecuteAsync(
            "SELECT ?",
            CreateParameters(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        handler.SerializedRequest.Should().NotBeNull();
        handler.SerializedRequest!.RootElement
            .GetProperty("requests")[0]
            .GetProperty("stmt")
            .GetProperty("args")[0]
            .GetProperty("value")
            .GetString()
            .Should().Be("generated");
        result.Rows.Should().ContainSingle().Which.Should().ContainSingle()
            .Which.Type.Should().Be("text");
        result.Rows[0][0].Value.GetString().Should().Be("generated");
    }

    private static AhtolaParameterCollection CreateParameters()
    {
        var parameters = new AhtolaParameterCollection();
        parameters.Add(new AhtolaParameter { Value = "generated" });
        return parameters;
    }

    private sealed class GeneratedJsonHandler : HttpMessageHandler
    {
        public JsonDocument? SerializedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SerializedRequest = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"value","decltype":"TEXT"}],"rows":[[{"type":"text","value":"generated"}]],"affected_row_count":0}}}]}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
