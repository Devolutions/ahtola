using System.Net;
using System.Text;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class CloudTransportQualificationTests
{
    [TestCase(HttpStatusCode.Unauthorized, false)]
    [TestCase(HttpStatusCode.Forbidden, false)]
    [TestCase(HttpStatusCode.Conflict, false)]
    [TestCase(HttpStatusCode.RequestTimeout, true)]
    [TestCase(HttpStatusCode.TooManyRequests, true)]
    [TestCase(HttpStatusCode.InternalServerError, true)]
    [TestCase((HttpStatusCode)599, true)]
    [TestCase((HttpStatusCode)600, false)]
    public void AutomaticReplicaRetryClassifiesOnlyTransientHttpFailures(
        HttpStatusCode statusCode,
        bool expectedTransient)
    {
        var exception = new AhtolaException("HTTP failure", statusCode);

        AhtolaConnection.IsTransientAutomaticSyncFailure(exception, CancellationToken.None)
            .Should().Be(expectedTransient);
        AhtolaConnection.IsTransientAutomaticSyncFailure(
                new HttpRequestException("HTTP failure", inner: null, statusCode),
                CancellationToken.None)
            .Should().Be(expectedTransient);
    }

    [Test]
    public void AutomaticReplicaRetryDoesNotRetryProtocolOrCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AhtolaConnection.IsTransientAutomaticSyncFailure(
                new InvalidDataException("malformed pull stream"),
                CancellationToken.None)
            .Should().BeFalse();
        AhtolaConnection.IsTransientAutomaticSyncFailure(
                new AhtolaReplicaConflictException("conflict"),
                CancellationToken.None)
            .Should().BeFalse();
        AhtolaConnection.IsTransientAutomaticSyncFailure(
                new TaskCanceledException(),
                cancellation.Token)
            .Should().BeFalse();
    }

    [Test]
    public void AutomaticReplicaRetryBackoffIsBounded()
    {
        AhtolaConnection.AutomaticSyncMaximumAttempts.Should().Be(3);
        AhtolaConnection.GetAutomaticSyncRetryDelay(0).Should().Be(TimeSpan.FromMilliseconds(50));
        AhtolaConnection.GetAutomaticSyncRetryDelay(1).Should().Be(TimeSpan.FromMilliseconds(100));
        AhtolaConnection.GetAutomaticSyncRetryDelay(2).Should().Be(TimeSpan.FromMilliseconds(200));
        AhtolaConnection.GetAutomaticSyncRetryDelay(20).Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Test]
    public void ReplicaPushFailureClassifyRecognizesConflictExceptionsRegardlessOfHttpStatus()
    {
        AhtolaReplicaPushFailure.Classify(new AhtolaReplicaConflictException("row conflict"))
            .Should().Be(AhtolaReplicaPushFailureKind.Conflict);
        AhtolaReplicaPushFailure.Classify(
                new AhtolaReplicaConflictException("HTTP conflict", remoteErrorCode: "SQLITE_CONSTRAINT"))
            .Should().Be(AhtolaReplicaPushFailureKind.Conflict);
    }

    [TestCase(HttpStatusCode.RequestTimeout, AhtolaReplicaPushFailureKind.TransientTransport)]
    [TestCase(HttpStatusCode.TooManyRequests, AhtolaReplicaPushFailureKind.TransientTransport)]
    [TestCase(HttpStatusCode.InternalServerError, AhtolaReplicaPushFailureKind.TransientTransport)]
    [TestCase(HttpStatusCode.BadRequest, AhtolaReplicaPushFailureKind.InvalidLocalState)]
    [TestCase(HttpStatusCode.Unauthorized, AhtolaReplicaPushFailureKind.InvalidLocalState)]
    public void ReplicaPushFailureClassifyMapsHttpStatusesConsistentlyForBothExceptionShapes(
        HttpStatusCode statusCode,
        AhtolaReplicaPushFailureKind expectedKind)
    {
        AhtolaReplicaPushFailure.Classify(new AhtolaException("HTTP failure", statusCode, replicaPush: true))
            .Should().Be(expectedKind);
        AhtolaReplicaPushFailure.Classify(new HttpRequestException("HTTP failure", inner: null, statusCode))
            .Should().Be(expectedKind);
    }

    [Test]
    public void ReplicaPushFailureClassifyTreatsCancellationAndNoResponseTransportFailuresAsTransient()
    {
        AhtolaReplicaPushFailure.Classify(new TaskCanceledException())
            .Should().Be(AhtolaReplicaPushFailureKind.TransientTransport);
        AhtolaReplicaPushFailure.Classify(new OperationCanceledException())
            .Should().Be(AhtolaReplicaPushFailureKind.TransientTransport);
        AhtolaReplicaPushFailure.Classify(new HttpRequestException("connection reset"))
            .Should().Be(AhtolaReplicaPushFailureKind.TransientTransport);
    }

    [Test]
    public void ReplicaPushFailureClassifyTreatsUnrecognizedAndProtocolFailuresAsInvalidLocalState()
    {
        AhtolaReplicaPushFailure.Classify(new InvalidDataException("malformed pull stream"))
            .Should().Be(AhtolaReplicaPushFailureKind.InvalidLocalState);
        AhtolaReplicaPushFailure.Classify(new AhtolaException("plain failure"))
            .Should().Be(AhtolaReplicaPushFailureKind.InvalidLocalState);
        AhtolaReplicaPushFailure.Classify(new InvalidOperationException("unexpected"))
            .Should().Be(AhtolaReplicaPushFailureKind.InvalidLocalState);
    }

    [Test]
    public void ReplicaPushFailureClassifyRejectsNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AhtolaReplicaPushFailure.Classify(null!));
    }

    [Test]
    public async Task RemotePipelineHonorsAResponseBaseUrl()
    {
        using var handler = new BaseUrlHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.test/cluster"),
            authToken: null);

        await client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: false,
            CancellationToken.None);
        await client.ExecuteAsync(
            "SELECT 2",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: true,
            CancellationToken.None);

        handler.Paths.Should().Equal("/cluster/v2/pipeline", "/redirected/v2/pipeline");
    }

    [Test]
    public async Task RemotePipelineRejectsACrossOriginResponseBaseUrl()
    {
        using var handler = new CrossOriginBaseUrlHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AhtolaRemoteClient(
            httpClient,
            new Uri("https://example.test/cluster"),
            authToken: "secret");

        Func<Task> act = () => client.ExecuteAsync(
            "SELECT 1",
            new AhtolaParameterCollection(),
            wantRows: true,
            commandTimeout: 30,
            closeAfter: false,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*origin*");
        handler.CallCount.Should().Be(1);
    }

    private sealed class BaseUrlHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var response = Paths.Count == 1
                ? """
                  {"baton":"baton-1","base_url":"/redirected","results":[{"type":"ok","response":{"type":"execute","result":{"cols":[],"rows":[],"affected_row_count":0}}}]}
                  """
                : """
                  {"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[],"rows":[],"affected_row_count":0}}}]}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CrossOriginBaseUrlHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            const string response =
                "{\"baton\":\"baton-1\",\"base_url\":\"https://attacker.test/redirected\","
                + "\"results\":[{\"type\":\"ok\",\"response\":{\"type\":\"execute\","
                + "\"result\":{\"cols\":[],\"rows\":[],\"affected_row_count\":0}}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }
}
