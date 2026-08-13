using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class RemoteBatchConditionTests
{
    [Test]
    public async Task RemoteBatchSerializesNestedStepConditionsAndAlignsSkippedResults()
    {
        using var handler = new ConditionHandler(
            """
            {"results":[{"type":"ok","response":{"type":"batch","result":{"step_results":[{"cols":[],"rows":[],"affected_row_count":1},null],"step_errors":[null,null]}}}]}
            """);
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(httpClient, new Uri("https://example.com"), authToken: null);
        var commands = new[]
        {
            new AhtolaBatchCommand("INSERT INTO t VALUES (1)"),
            new AhtolaBatchCommand("INSERT INTO t VALUES (2)")
            {
                RemoteCondition = AhtolaRemoteBatchCondition.And(
                    AhtolaRemoteBatchCondition.StepSucceeded(0),
                    AhtolaRemoteBatchCondition.Not(AhtolaRemoteBatchCondition.IsAutocommit),
                    AhtolaRemoteBatchCondition.Or(
                        AhtolaRemoteBatchCondition.StepFailed(0),
                        AhtolaRemoteBatchCondition.StepSucceeded(0))),
            },
        };

        var results = await client.ExecuteBatchAsync(
            commands,
            commandTimeout: 30,
            wantRows: false,
            closeAfter: true,
            CancellationToken.None);

        results.Should().HaveCount(2);
        results.Select(static result => result.AffectedRowCount).Should().Equal(1UL, 0UL);

        var steps = handler.BatchSteps;
        steps[0].TryGetProperty("condition", out _).Should().BeFalse();
        var condition = steps[1].GetProperty("condition");
        condition.GetProperty("type").GetString().Should().Be("and");
        var operands = condition.GetProperty("conds").EnumerateArray().ToArray();
        operands[0].GetProperty("type").GetString().Should().Be("ok");
        operands[0].GetProperty("step").GetInt32().Should().Be(0);
        operands[1].GetProperty("type").GetString().Should().Be("not");
        operands[1].GetProperty("cond").GetProperty("type").GetString().Should().Be("is_autocommit");
        operands[2].GetProperty("type").GetString().Should().Be("or");
        var alternatives = operands[2].GetProperty("conds").EnumerateArray().ToArray();
        alternatives.Select(static item => item.GetProperty("type").GetString()).Should().Equal("error", "ok");
        alternatives.Select(static item => item.GetProperty("step").GetInt32()).Should().Equal(0, 0);
    }

    [Test]
    public void LocalBatchesRejectRemoteConditionsInsteadOfIgnoringThem()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        using var batch = new AhtolaBatch(connection);
        batch.BatchCommands.Add(new AhtolaBatchCommand("SELECT 1")
        {
            RemoteCondition = AhtolaRemoteBatchCondition.IsAutocommit,
        });

        Assert.Throws<NotSupportedException>(() => batch.ExecuteNonQuery())!
            .Message.Should().Be("RemoteCondition requires a remote Ahtola connection.");
    }

    [Test]
    public void RemoteBatchConditionRejectsInvalidShapes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AhtolaRemoteBatchCondition.StepSucceeded(-1));
        Assert.Throws<ArgumentException>(() => AhtolaRemoteBatchCondition.And());
        Assert.Throws<ArgumentNullException>(() => AhtolaRemoteBatchCondition.Or(null!));
    }

    private sealed class ConditionHandler(string response) : HttpMessageHandler
    {
        public JsonElement[] BatchSteps { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            BatchSteps = document.RootElement
                .GetProperty("requests")[0]
                .GetProperty("batch")
                .GetProperty("steps")
                .EnumerateArray()
                .Select(static step => step.Clone())
                .ToArray();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
