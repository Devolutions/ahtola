using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class HranaV3StreamingTests
{
    [Test]
    public async Task CursorYieldsRowsBeforeTheResponseCompletes()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        stream.Add(Row(1));
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM large_table";

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        stream.IsCompleted.Should().BeFalse();

        stream.Add(Row(2));
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task CursorStreamsLargeResponsesWithoutReadingThemAtOpen()
    {
        const int rowCount = 20_000;
        var body = new StringBuilder(CursorHeader())
            .Append(StepBegin());
        for (var value = 0; value < rowCount; value++)
            body.Append(Row(value));
        body.Append(StepEnd()).Append(Terminator());

        var payload = body.ToString();
        var stream = new ControlledReadStream();
        stream.Add(payload);
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM large_table";

        await using var reader = await command.ExecuteReaderAsync();
        stream.TotalBytesRead.Should().BeLessThan(Encoding.UTF8.GetByteCount(payload));

        var rowsRead = 0;
        while (await reader.ReadAsync())
            rowsRead++;

        rowsRead.Should().Be(rowCount);
    }

    [Test]
    public async Task CursorReadCancellationAbortsAndClosesTheBaton()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM blocked_query";
        await using var reader = await command.ExecuteReaderAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var read = async () => await reader.ReadAsync(cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        handler.Paths.Should().Equal("/v3/pipeline", "/v3/cursor", "/v3/pipeline");
        AssertCloseRequest(handler.Bodies[2], "cursor-baton");
    }

    [Test]
    public async Task CommandCancelInterruptsABlockedCursorRead()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM blocked_query";
        await using var reader = await command.ExecuteReaderAsync();

        var read = reader.ReadAsync();
        await stream.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        command.Cancel();

        Func<Task> waitForRead = async () => await read;
        await waitForRead.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task CursorTimeoutResetsForEachNetworkRead()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        stream.Add(Row(1));
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM slow_consumer";
        command.CommandTimeout = 1;
        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        stream.Add(Row(2));
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task ActiveCursorRejectsConcurrentBatonUse()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineExecute(2));
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var readerCommand = connection.CreateCommand();
        readerCommand.CommandText = "SELECT value FROM blocked_query";
        await using var reader = await readerCommand.ExecuteReaderAsync();
        using var secondCommand = connection.CreateCommand();
        secondCommand.CommandText = "SELECT 2";

        var execute = async () => await secondCommand.ExecuteScalarAsync();

        await execute.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A remote operation is already in progress*");
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();
    }

    [Test]
    public async Task CursorRejectsMalformedFramesAndClosesTheBaton()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        stream.Add("not-json\n");
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM malformed_query";
        await using var reader = await command.ExecuteReaderAsync();

        var read = async () => await reader.ReadAsync();

        await read.Should().ThrowAsync<AhtolaException>()
            .WithMessage("Unable to parse remote cursor response:*");
        handler.Paths.Should().Equal("/v3/pipeline", "/v3/cursor", "/v3/pipeline");
        AssertCloseRequest(handler.Bodies[2], "cursor-baton");
    }

    [Test]
    public async Task MissingV3CursorFallsBackOnceToBufferedV2Pipeline()
    {
        using var handler = new CannedHranaHandler((path, _, _) => path switch
        {
            "/v3/cursor" => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"unknown endpoint"}""", Encoding.UTF8, "application/json"),
            },
            "/v2/pipeline" => PipelineExecute(42),
            _ => throw new InvalidOperationException($"Unexpected path {path}."),
        });
        using var connection = CreateConnection(handler, readYourWrites: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42";

        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(42);
        (await reader.ReadAsync()).Should().BeFalse();
        handler.Paths.Should().Equal("/v3/cursor", "/v2/pipeline");
        using var fallbackRequest = JsonDocument.Parse(handler.Bodies[1]);
        fallbackRequest.RootElement.GetProperty("requests")[0].GetProperty("type").GetString()
            .Should().Be("execute");
        fallbackRequest.RootElement.GetProperty("requests")[1].GetProperty("type").GetString()
            .Should().Be("close");
    }

    [Test]
    public async Task StatelessCursorClosesItsIssuedBatonAfterTheTerminator()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        stream.Add(Row(7));
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 7";

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeFalse();

        handler.Paths.Should().Equal("/v3/cursor", "/v3/pipeline");
        AssertCloseRequest(handler.Bodies[1], "cursor-baton");
    }

    [Test]
    public async Task CleanupCloseFailureDoesNotChangeASuccessfulCursorResult()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBegin());
        stream.Add(Row(7));
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor"
                ? Ndjson(stream)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("close failed", Encoding.UTF8, "text/plain"),
                });
        using var connection = CreateConnection(handler, readYourWrites: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 7";

        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(7);
        (await reader.ReadAsync()).Should().BeFalse();
        handler.Paths.Should().Equal("/v3/cursor", "/v3/pipeline");
    }

    [Test]
    public async Task CursorFieldTypeSkipsLeadingNullWithoutLosingRows()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(StepBeginWithoutDeclaredType());
        stream.Add(NullRow());
        stream.Add(Row(9));
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT nullable_expression";
        await using var reader = await command.ExecuteReaderAsync();

        reader.GetFieldType(0).Should().Be(typeof(long));
        (await reader.ReadAsync()).Should().BeTrue();
        reader.IsDBNull(0).Should().BeTrue();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(9);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Test]
    public async Task CursorFieldTypeUsesBoundedLookaheadForAllNullResults()
    {
        const int rowCount = 20_000;
        var body = new StringBuilder(CursorHeader())
            .Append(StepBeginWithoutDeclaredType());
        for (var i = 0; i < rowCount; i++)
            body.Append(NullRow());
        body.Append(StepEnd()).Append(Terminator());

        var payload = body.ToString();
        var stream = new ControlledReadStream();
        stream.Add(payload);
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT nullable_expression";
        await using var reader = await command.ExecuteReaderAsync();

        reader.GetFieldType(0).Should().Be(typeof(object));
        stream.TotalBytesRead.Should().BeLessThan(Encoding.UTF8.GetByteCount(payload));
        (await reader.ReadAsync()).Should().BeTrue();
        reader.IsDBNull(0).Should().BeTrue();
    }

    [Test]
    public async Task CursorRejectsRowsBeforeStepBegin()
    {
        var stream = new ControlledReadStream();
        stream.Add(CursorHeader());
        stream.Add(Row(42));
        stream.Add(StepBegin());
        stream.Add(StepEnd());
        stream.Add(Terminator());
        stream.Complete();
        using var handler = new CannedHranaHandler((path, _, _) =>
            path == "/v3/cursor" ? Ndjson(stream) : PipelineClose());
        using var connection = CreateConnection(handler, readYourWrites: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM malformed_query";

        var execute = async () => await command.ExecuteReaderAsync();

        await execute.Should().ThrowAsync<AhtolaException>()
            .WithMessage("Unable to parse remote cursor response: row was received before step_begin*");
        handler.Paths.Should().Equal("/v3/cursor", "/v3/pipeline");
        AssertCloseRequest(handler.Bodies[1], "cursor-baton");
    }

    private static AhtolaConnection CreateConnection(HttpMessageHandler handler, bool readYourWrites)
    {
        var client = new AhtolaRemoteClient(
            new HttpClient(handler),
            new Uri("https://example.test"),
            authToken: null);
        return new AhtolaConnection(
            $"Data Source=https://example.test;Read Your Writes={readYourWrites}",
            client);
    }

    private static HttpResponseMessage Ndjson(Stream stream)
        => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };

    private static HttpResponseMessage PipelineClose()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"baton":null,"results":[{"type":"ok","response":{"type":"close"}}]}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage PipelineExecute(long value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"baton":null,"results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"value","decltype":"INTEGER"}],"rows":[[{"type":"integer","value":"__VALUE__"}]],"affected_row_count":0}}},{"type":"ok","response":{"type":"close"}}]}
                """.Replace(
                    "__VALUE__",
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json"),
        };

    private static void AssertCloseRequest(string json, string baton)
    {
        using var request = JsonDocument.Parse(json);
        request.RootElement.GetProperty("baton").GetString().Should().Be(baton);
        request.RootElement.GetProperty("requests").GetArrayLength().Should().Be(1);
        request.RootElement.GetProperty("requests")[0].GetProperty("type").GetString()
            .Should().Be("close");
    }

    private static string CursorHeader()
        => """{"baton":"cursor-baton","base_url":null}""" + "\n";

    private static string StepBegin()
        => """{"type":"step_begin","step":0,"cols":[{"name":"value","decltype":"INTEGER"}]}""" + "\n";

    private static string StepBeginWithoutDeclaredType()
        => """{"type":"step_begin","step":0,"cols":[{"name":"value","decltype":null}]}""" + "\n";

    private static string Row(long value)
        => $$"""{"type":"row","row":[{"type":"integer","value":"{{value}}"}]}""" + "\n";

    private static string NullRow()
        => """{"type":"row","row":[{"type":"null"}]}""" + "\n";

    private static string StepEnd()
        => """{"type":"step_end","affected_row_count":0,"last_insert_rowid":null}""" + "\n";

    private static string Terminator()
        => """{"type":"replication_index","replication_index":null}""" + "\n";

    private sealed class CannedHranaHandler(
        Func<string, string, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Paths.Add(path);
            Bodies.Add(body);
            return responder(path, body, Paths.Count - 1);
        }
    }

    private sealed class ControlledReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private byte[]? _current;
        private int _currentOffset;

        public bool IsCompleted { get; private set; }

        public long TotalBytesRead { get; private set; }

        public TaskCompletionSource BlockedReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(string value)
        {
            if (!_chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(value)))
                throw new InvalidOperationException("The response stream is already complete.");
        }

        public void Complete()
        {
            IsCompleted = true;
            _chunks.Writer.TryComplete();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _currentOffset == _current.Length)
            {
                if (!_chunks.Reader.TryPeek(out _))
                    BlockedReadStarted.TrySetResult();
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (!_chunks.Reader.TryRead(out _current))
                    continue;
                _currentOffset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            TotalBytesRead += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
