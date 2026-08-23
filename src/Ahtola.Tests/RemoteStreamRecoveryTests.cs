using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class RemoteStreamRecoveryTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task StatelessExecuteRetriesOneExpiredStreamOnAFreshSession(bool async)
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("stale", 1),
            StreamExpired("stale stream"),
            ExecuteSuccess("fresh", 2));
        using var connection = CreateConnection(handler);

        (await ExecuteScalar(connection, async)).Should().Be(1L);
        (await ExecuteScalar(connection, async)).Should().Be(2L);

        handler.Requests.Should().HaveCount(3);
        handler.GetBaton(1).Should().Be("stale");
        handler.GetBaton(2).Should().BeNull();
    }

    [Test]
    public async Task StatelessBatchRetriesOneExpiredStreamOnAFreshSession()
    {
        using var handler = new ScriptedPipelineHandler(
            BatchSuccess("stale", replicationIndex: "7"),
            StreamExpired("stale stream"),
            BatchSuccess("fresh"));
        using var connection = CreateConnection(handler);
        await using var batch = new AhtolaBatch(connection);
        batch.BatchCommands.Add(new AhtolaBatchCommand("UPDATE t SET value = 1"));

        (await batch.ExecuteNonQueryAsync(CancellationToken.None)).Should().Be(1);
        (await batch.ExecuteNonQueryAsync(CancellationToken.None)).Should().Be(1);

        handler.Requests.Should().HaveCount(3);
        handler.GetBaton(1).Should().Be("stale");
        handler.GetBaton(2).Should().BeNull();
        handler.GetBatchReplicationIndex(2).Should().Be("7");
    }

    [Test]
    public async Task StatelessExecuteStopsAfterOneExpiryRetryAndPreservesServerDetails()
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("stale", 1),
            StreamExpired("first expiry"),
            StreamExpired("second expiry"));
        using var connection = CreateConnection(handler);
        await ExecuteScalar(connection, async: true);

        var exception = Assert.ThrowsAsync<AhtolaRemoteSqlException>(
            async () => await ExecuteScalar(connection, async: true));

        exception!.RemoteErrorCode.Should().Be("STREAM_EXPIRED");
        exception.RemoteErrorMessage.Should().Be("second expiry");
        exception.Message.Should().Contain("second expiry").And.Contain("STREAM_EXPIRED");
        handler.Requests.Should().HaveCount(3);
    }

    [Test]
    public async Task NonExpiryRemoteErrorsAreNotRetried()
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("stale", 1),
            RemoteError("SQLITE_CONSTRAINT", "constraint failed"));
        using var connection = CreateConnection(handler);
        await ExecuteScalar(connection, async: true);

        var exception = Assert.ThrowsAsync<AhtolaRemoteSqlException>(
            async () => await ExecuteScalar(connection, async: true));

        exception!.IsStreamExpired.Should().BeFalse();
        exception.RemoteErrorCode.Should().Be("SQLITE_CONSTRAINT");
        handler.Requests.Should().HaveCount(2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoteTransactionPinsModeAndSupportsExplicitCompletion(bool async)
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("tx-1", 0),
            ExecuteSuccess("done-1", 0),
            ExecuteSuccess("tx-2", 0),
            ExecuteSuccess("done-2", 0));
        using var connection = CreateConnection(handler);

        if (async)
        {
            await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable))
                await transaction.CommitAsync();
            await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadUncommitted))
                await transaction.RollbackAsync();
        }
        else
        {
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                transaction.Commit();
            using (var transaction = connection.BeginTransaction(IsolationLevel.ReadUncommitted))
                transaction.Rollback();
        }

        handler.GetSql(0).Should().Be("BEGIN IMMEDIATE");
        handler.GetSql(1).Should().Be("COMMIT");
        handler.GetSql(2).Should().Be("BEGIN");
        handler.GetSql(3).Should().Be("ROLLBACK");
    }

    [TestCase("commit")]
    [TestCase("rollback")]
    public void ExpiredActiveTransactionPreservesRootFailureAndBecomesUnusable(string completion)
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("transaction", 0),
            StreamExpired("transaction stream expired"));
        using var connection = CreateConnection(handler);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE t SET value = 1";

        var rootFailure = Assert.Throws<AhtolaRemoteSqlException>(() => command.ExecuteNonQuery())!;
        rootFailure.RemoteErrorCode.Should().Be("STREAM_EXPIRED");
        handler.Requests.Should().HaveCount(2);

        var unusable = Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery())!;
        unusable.InnerException.Should().BeSameAs(rootFailure);
        handler.Requests.Should().HaveCount(2);

        Assert.Throws<AhtolaRemoteSqlException>(() => transaction.Save("after_failure"))
            .Should().BeSameAs(rootFailure);
        handler.Requests.Should().HaveCount(2);

        var reported = completion == "commit"
            ? Assert.Throws<AhtolaRemoteSqlException>(() => transaction.Commit())
            : Assert.Throws<AhtolaRemoteSqlException>(() => transaction.Rollback());
        reported.Should().BeSameAs(rootFailure);
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public void ExpiredSchemaProbeFaultsActiveTransactionBeforeCursorStarts()
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("transaction", 0),
            StreamExpired("schema probe stream expired"));
        using var connection = CreateConnection(handler);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM widgets";

        var rootFailure = Assert.Throws<AhtolaRemoteSqlException>(() => command.ExecuteReader())!;

        rootFailure.RemoteErrorCode.Should().Be("STREAM_EXPIRED");
        handler.Requests.Should().HaveCount(2);
        handler.GetSql(1).Should().StartWith("PRAGMA table_info");
        Assert.Throws<AhtolaRemoteSqlException>(() => transaction.Rollback())
            .Should().BeSameAs(rootFailure);
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public void FaultedTransactionDisposalDoesNotMaskAnExceptionAlreadyUnwinding()
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("transaction", 0),
            StreamExpired("transaction stream expired"));
        using var connection = CreateConnection(handler);

        var marker = Assert.Throws<MarkerException>(() =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE t SET value = 1";
            Assert.Throws<AhtolaRemoteSqlException>(() => command.ExecuteNonQuery());
            throw new MarkerException();
        });

        marker.Should().NotBeNull();
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public void SqliteFacadeFaultedTransactionDisposalDoesNotMaskAnExceptionAlreadyUnwinding()
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("transaction", 0),
            StreamExpired("transaction stream expired"));
        var priorFactory = Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory;
        Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new Ahtola.Data.Sqlite.SqliteConnection(
                "Data Source=https://example.test;Read Your Writes=True");
            connection.Open();

            var marker = Assert.Throws<MarkerException>(() =>
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE t SET value = 1";
                Assert.Throws<Ahtola.Data.Sqlite.SqliteRemoteException>(() => command.ExecuteNonQuery());
                throw new MarkerException();
            });

            marker.Should().NotBeNull();
            connection.Transaction.Should().BeNull();
            handler.Requests.Should().HaveCount(2);
        }
        finally
        {
            Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    [Test]
    public void SqliteFacadeFaultedCommitCompletesBeforeDisposal()
    {
        using var handler = new ScriptedPipelineHandler(
            ExecuteSuccess("transaction", 0),
            StreamExpired("transaction stream expired"));
        var priorFactory = Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory;
        Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory = () => handler;
        try
        {
            using var connection = new Ahtola.Data.Sqlite.SqliteConnection(
                "Data Source=https://example.test;Read Your Writes=True");
            connection.Open();
            var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE t SET value = 1";
            Assert.Throws<Ahtola.Data.Sqlite.SqliteRemoteException>(() => command.ExecuteNonQuery());

            Assert.Throws<Ahtola.Data.Sqlite.SqliteRemoteException>(() => transaction.Commit());
            transaction.Dispose();

            connection.Transaction.Should().BeNull();
            handler.Requests.Should().HaveCount(2);
        }
        finally
        {
            Ahtola.Data.Sqlite.SqliteConnection.RemoteMessageHandlerFactory = priorFactory;
        }
    }

    private static AhtolaConnection CreateConnection(HttpMessageHandler handler)
    {
        var client = new AhtolaRemoteClient(
            new HttpClient(handler),
            new Uri("https://example.test"),
            authToken: null);
        return new AhtolaConnection(
            "Data Source=https://example.test;Read Your Writes=True",
            client);
    }

    private static async Task<object?> ExecuteScalar(AhtolaConnection connection, bool async)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        return async
            ? await command.ExecuteScalarAsync(CancellationToken.None)
            : command.ExecuteScalar();
    }

    private static string ExecuteSuccess(string baton, long value)
        => """
           {"baton":"__BATON__","results":[{"type":"ok","response":{"type":"execute","result":{"cols":[{"name":"value","decltype":"INTEGER"}],"rows":[[{"type":"integer","value":"__VALUE__"}]],"affected_row_count":0}}}]}
           """
            .Replace("__BATON__", baton, StringComparison.Ordinal)
            .Replace("__VALUE__", value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string BatchSuccess(string baton, string? replicationIndex = null)
        => """
           {"baton":"__BATON__","results":[{"type":"ok","response":{"type":"batch","result":{"replication_index":__INDEX__,"step_results":[{"cols":[],"rows":[],"affected_row_count":1}],"step_errors":[null]}}}]}
           """
            .Replace("__BATON__", baton, StringComparison.Ordinal)
            .Replace(
                "__INDEX__",
                replicationIndex is null ? "null" : "\"" + replicationIndex + "\"",
                StringComparison.Ordinal);

    private static string StreamExpired(string message)
        => RemoteError("STREAM_EXPIRED", message);

    private static string RemoteError(string code, string message)
        => """
           {"results":[{"type":"error","error":{"message":"__MESSAGE__","code":"__CODE__"}}]}
           """
            .Replace("__MESSAGE__", message, StringComparison.Ordinal)
            .Replace("__CODE__", code, StringComparison.Ordinal);

    private sealed class ScriptedPipelineHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<JsonElement> Requests { get; } = [];

        public string? GetBaton(int index)
        {
            var root = Requests[index];
            if (!root.TryGetProperty("baton", out var baton))
                return null;
            return baton.ValueKind == JsonValueKind.Null
                ? null
                : baton.GetString();
        }

        public string GetSql(int index)
        {
            var root = Requests[index];
            var statement = root.TryGetProperty("requests", out var requests)
                ? requests[0].GetProperty("stmt")
                : root.GetProperty("batch").GetProperty("steps")[0].GetProperty("stmt");
            return statement.GetProperty("sql").GetString()!;
        }

        public string? GetBatchReplicationIndex(int index)
            => Requests[index]
                .GetProperty("requests")[0]
                .GetProperty("batch")
                .GetProperty("replication_index")
                .GetString();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement.Clone();
            Requests.Add(root);
            if (request.RequestUri!.AbsolutePath.EndsWith("/v3/cursor", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ToCursorResponse(_responses.Dequeue()),
                        Encoding.UTF8,
                        "application/x-ndjson"),
                };
            }

            var requestType = root.GetProperty("requests")[0].GetProperty("type").GetString();
            var response = requestType == "close"
                ? """{"results":[{"type":"ok","response":{"type":"close"}}]}"""
                : _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }

        private static string ToCursorResponse(string pipelineResponse)
        {
            using var document = JsonDocument.Parse(pipelineResponse);
            var root = document.RootElement;
            var baton = root.TryGetProperty("baton", out var batonElement)
                && batonElement.ValueKind == JsonValueKind.String
                    ? batonElement.GetString()
                    : "cursor-error";
            var lines = new StringBuilder()
                .Append("{\"baton\":")
                .Append(JsonSerializer.Serialize(baton))
                .AppendLine(",\"base_url\":null}");
            var result = root.GetProperty("results")[0];
            if (result.GetProperty("type").GetString() == "error")
            {
                lines.Append("{\"type\":\"step_error\",\"step\":0,\"error\":")
                    .Append(result.GetProperty("error").GetRawText())
                    .AppendLine("}");
                lines.AppendLine("""{"type":"replication_index","replication_index":null}""");
                return lines.ToString();
            }

            var statement = result.GetProperty("response").GetProperty("result");
            lines.Append("{\"type\":\"step_begin\",\"step\":0,\"cols\":")
                .Append(statement.GetProperty("cols").GetRawText())
                .AppendLine("}");
            foreach (var row in statement.GetProperty("rows").EnumerateArray())
            {
                lines.Append("{\"type\":\"row\",\"row\":")
                    .Append(row.GetRawText())
                    .AppendLine("}");
            }
            lines.Append("{\"type\":\"step_end\",\"affected_row_count\":")
                .Append(statement.GetProperty("affected_row_count").GetRawText())
                .AppendLine(",\"last_insert_rowid\":null}");
            lines.AppendLine("""{"type":"replication_index","replication_index":null}""");
            return lines.ToString();
        }
    }

    private sealed class MarkerException : Exception;
}
