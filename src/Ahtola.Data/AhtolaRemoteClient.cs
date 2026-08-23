using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ahtola;

internal sealed partial class AhtolaRemoteClient : IDisposable
{
    /// <summary>
    /// HTTP header used to convey the remote encryption key alongside a push/pull request. Also
    /// used by <see cref="ManagedReplicaBootstrapper"/>'s raw HTTP bootstrap/pull requests so both
    /// paths speak the identical remote-encryption wire protocol.
    /// </summary>
    internal const string EncryptionKeyHeaderName = "x-turso-encryption-key";

    private readonly HttpClient _httpClient;
    private readonly string? _authToken;
    private readonly string? _remoteEncryptionKey;
    private readonly bool _disposeHttpClient;
    private Uri _pipelineUri;
    private Uri _cursorUri;
    private readonly bool _allowV2Fallback;
    private RemoteProtocolVersion _protocolVersion;
    private readonly object _requestGate = new();
    private bool _requestInFlight;
    private string? _baton;
    private ulong? _replicationIndex;

    public AhtolaRemoteClient(
        Uri endpoint,
        string? authToken,
        AhtolaRemoteEncryptionOptions? remoteEncryption = null)
        : this(
            AhtolaRemoteTransportSecurity.CreateRedirectSafeHttpClient(),
            endpoint,
            authToken,
            remoteEncryption,
            disposeHttpClient: true,
            automaticRedirectsDisabled: true)
    {
    }

    internal AhtolaRemoteClient(
        HttpClient httpClient,
        Uri endpoint,
        string? authToken,
        AhtolaRemoteEncryptionOptions? remoteEncryption = null,
        bool disposeHttpClient = false,
        bool automaticRedirectsDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);

        _httpClient = httpClient;
        (_protocolVersion, _allowV2Fallback) = DetectProtocol(endpoint);
        _pipelineUri = CreateProtocolUri(
            endpoint,
            _protocolVersion == RemoteProtocolVersion.V2 ? "/v2/pipeline" : "/v3/pipeline");
        _cursorUri = CreateProtocolUri(endpoint, "/v3/cursor");
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
        _remoteEncryptionKey = remoteEncryption?.Base64Key;
        AhtolaRemoteTransportSecurity.Validate(
            _pipelineUri,
            _authToken,
            remoteEncryptionConfigured: _remoteEncryptionKey is not null);
        AhtolaRemoteTransportSecurity.ValidateRedirectContract(
            automaticRedirectsDisabled,
            remoteEncryptionConfigured: _remoteEncryptionKey is not null);
        _disposeHttpClient = disposeHttpClient;
    }

    public bool HasOpenSession => _baton is not null;

    public void ResetSession()
    {
        _baton = null;
    }

    public async Task<RemoteStatementResult> ExecuteAsync(
        string sql,
        AhtolaParameterCollection parameters,
        bool wantRows,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        ValidateParameters(sql, parameters);
        var request = new RemotePipelineRequest
        {
            Baton = _baton,
            Requests =
            [
                RemoteStreamRequest.Execute(BuildStatement(sql, parameters, wantRows)),
            ],
        };

        if (closeAfter)
            request.Requests.Add(RemoteStreamRequest.Close());

        var response = await SendPipelineAsync(request, commandTimeout, cancellationToken).ConfigureAwait(false);
        UpdateSession(response, closeAfter);
        return ExtractExecuteResult(response);
    }

    public async Task<RemoteReaderExecution> ExecuteCursorAsync(
        string sql,
        AhtolaParameterCollection parameters,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken,
        Action<Exception>? failureCallback = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        ValidateParameters(sql, parameters);
        if (_protocolVersion == RemoteProtocolVersion.V2)
        {
            var buffered = await ExecuteAsync(
                    sql,
                    parameters,
                    wantRows: true,
                    commandTimeout,
                    closeAfter,
                    cancellationToken)
                .ConfigureAwait(false);
            return RemoteReaderExecution.FromBuffered(buffered);
        }

        var request = new RemoteCursorRequest
        {
            Baton = _baton,
            Batch = new RemoteBatch
            {
                Steps =
                [
                    new RemoteBatchStep
                    {
                        Statement = BuildStatement(sql, parameters, wantRows: true),
                    },
                ],
                ReplicationIndex = _replicationIndex?.ToString(CultureInfo.InvariantCulture),
            },
        };

        var cursor = await SendCursorAsync(
                request,
                commandTimeout,
                closeAfter,
                cancellationToken,
                failureCallback)
            .ConfigureAwait(false);
        return cursor is null
            ? RemoteReaderExecution.FromBuffered(
                await ExecuteAsync(
                        sql,
                        parameters,
                        wantRows: true,
                        commandTimeout,
                        closeAfter,
                        cancellationToken)
                    .ConfigureAwait(false))
            : RemoteReaderExecution.FromCursor(cursor);
    }

    public async Task<IReadOnlyList<RemoteStatementResult>> ExecuteBatchAsync(
        IReadOnlyList<AhtolaBatchCommand> commands,
        int commandTimeout,
        bool wantRows,
        bool closeAfter,
        CancellationToken cancellationToken,
        Action<int>? stepSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new InvalidOperationException("Batch must contain at least one command.");
        ValidateParameters(commands);

        var steps = new List<RemoteBatchStep>(commands.Count);
        foreach (var command in commands)
        {
            steps.Add(new RemoteBatchStep
            {
                Condition = command.RemoteCondition is null ? null : BuildCondition(command.RemoteCondition),
                Statement = BuildStatement(command.CommandText, command.Parameters, wantRows),
            });
        }

        var request = new RemotePipelineRequest
        {
            Baton = _baton,
            Requests =
            [
                RemoteStreamRequest.Batch(new RemoteBatch
                {
                    Steps = steps,
                    ReplicationIndex = _replicationIndex?.ToString(CultureInfo.InvariantCulture),
                }),
            ],
        };

        if (closeAfter)
            request.Requests.Add(RemoteStreamRequest.Close());

        var response = await SendPipelineAsync(request, commandTimeout, cancellationToken).ConfigureAwait(false);
        UpdateSession(response, closeAfter);
        return ExtractBatchResults(response, commands.Count, stepSucceeded);
    }

    /// <summary>
    /// Replays a durably captured managed-replica batch using the same guarded Hrana batch
    /// shape as Turso v0.7.2's <c>send_push_batch</c>. A caller acknowledges its local journal
    /// only after this method returns successfully.
    /// </summary>
    internal async Task PushReplicaChangesAsync(
        ReplicaLocalChangeBatch changes,
        string clientId,
        long sourcePullGeneration,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (changes.Changes.Count == 0)
            return;

        var guarded = new RemoteBatchCondition
        {
            Type = "not",
            Condition = new RemoteBatchCondition { Type = "is_autocommit" },
        };
        var steps = new List<RemoteBatchStep>(checked(changes.Changes.Count + 4))
        {
            new()
            {
                Statement = BuildStatement("BEGIN IMMEDIATE", new AhtolaParameterCollection(), wantRows: false),
            },
            new()
            {
                Condition = guarded,
                Statement = BuildStatement(
                    "CREATE TABLE IF NOT EXISTS turso_sync_last_change_id (client_id TEXT PRIMARY KEY, pull_gen INTEGER, change_id INTEGER)",
                    new AhtolaParameterCollection(),
                    wantRows: false),
            },
        };

        var replayedChangeSteps = new List<int>(changes.Changes.Count);
        var replayedChangeContexts = new Dictionary<int, ReplicaPushConflictContext>();
        foreach (var change in changes.Changes)
        {
            // A multi-row statement is represented by more than one update hook record. Only
            // its first record carries SQL, while all of its records advance together on ACK.
            if (string.IsNullOrWhiteSpace(change.Sql))
                continue;

            var step = steps.Count;
            replayedChangeSteps.Add(step);
            replayedChangeContexts.Add(
                step,
                new ReplicaPushConflictContext(
                    change.Kind == ReplicaLocalChangeKind.Schema
                        ? AhtolaReplicaConflictKind.SchemaChange
                        : AhtolaReplicaConflictKind.RowWrite,
                    change.Sequence));
            steps.Add(new RemoteBatchStep
            {
                Condition = guarded,
                Statement = BuildStatement(change.Sql, new AhtolaParameterCollection(), wantRows: false),
            });
        }

        if (replayedChangeSteps.Count == 0)
            throw new InvalidDataException("Managed replica journal batch has no replayable SQL.");

        var watermarkStep = steps.Count;
        var watermarkParameters = new AhtolaParameterCollection();
        watermarkParameters.Add(clientId);
        watermarkParameters.Add(sourcePullGeneration);
        watermarkParameters.Add(changes.Changes[^1].Sequence);
        var watermarkStatement = BuildStatement(
            "INSERT INTO turso_sync_last_change_id(client_id, pull_gen, change_id) VALUES (?, ?, ?) ON CONFLICT(client_id) DO UPDATE SET pull_gen=excluded.pull_gen, change_id=excluded.change_id",
            watermarkParameters,
            wantRows: false);
        steps.Add(new RemoteBatchStep { Condition = guarded, Statement = watermarkStatement });

        var commitStep = steps.Count;
        steps.Add(new RemoteBatchStep
        {
            Statement = BuildStatement("COMMIT", new AhtolaParameterCollection(), wantRows: false),
        });

        var request = new RemotePipelineRequest
        {
            Requests = [RemoteStreamRequest.Batch(new RemoteBatch { Steps = steps })],
        };
        var response = await SendPipelineAsync(request, commandTimeout, cancellationToken, replicaPush: true).ConfigureAwait(false);
        UpdateSession(response, closeAfter: false);

        var succeeded = new bool[steps.Count];
        _ = ExtractBatchResults(
            response,
            steps.Count,
            step => succeeded[step] = true,
            replicaPush: true,
            replayedChangeContexts: replayedChangeContexts);
        foreach (var step in replayedChangeSteps)
        {
            if (!succeeded[step])
                throw new AhtolaException("Remote replica push skipped a local change.", AhtolaReplicaPushFailureKind.InvalidLocalState);
        }
        if (!succeeded[watermarkStep] || !succeeded[commitStep])
            throw new AhtolaException("Remote replica push did not commit its acknowledgement watermark.", AhtolaReplicaPushFailureKind.InvalidLocalState);
    }

    internal async Task<(long PullGeneration, long ChangeId)?> ReadReplicaPushWatermarkAsync(
        string clientId,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var parameters = new AhtolaParameterCollection();
        parameters.Add(clientId);

        RemoteStatementResult result;
        try
        {
            result = await ExecuteAsync(
                    "SELECT pull_gen, change_id FROM turso_sync_last_change_id WHERE client_id = ?",
                    parameters,
                    wantRows: true,
                    commandTimeout,
                    closeAfter: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AhtolaRemoteSqlException exception) when (
            exception.RemoteErrorMessage?.Contains("no such table", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }

        if (result.Rows.Count == 0)
            return null;
        if (result.Rows.Count != 1 || result.Rows[0].Count != 2)
        {
            throw new AhtolaException(
                "Remote replica push acknowledgement returned an invalid result shape.",
                AhtolaReplicaPushFailureKind.InvalidLocalState);
        }

        var pullGeneration = result.Rows[0][0].GetInt64();
        var changeId = result.Rows[0][1].GetInt64();
        if (pullGeneration < 0 || changeId < 0)
        {
            throw new AhtolaException(
                "Remote replica push acknowledgement returned an invalid watermark.",
                AhtolaReplicaPushFailureKind.InvalidLocalState);
        }
        return (pullGeneration, changeId);
    }

    public async Task CloseAsync(int commandTimeout, CancellationToken cancellationToken)
    {
        if (_baton is null)
            return;

        var request = new RemotePipelineRequest
        {
            Baton = _baton,
            Requests = [RemoteStreamRequest.Close()],
        };

        var response = await SendPipelineAsync(request, commandTimeout, cancellationToken).ConfigureAwait(false);
        UpdateSession(response, closeAfter: false);
        ValidateCloseResult(response);
        _baton = null;
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
            _httpClient.Dispose();
    }

    private async Task<RemotePipelineResponse> SendPipelineAsync(
        RemotePipelineRequest request,
        int commandTimeout,
        CancellationToken cancellationToken,
        bool replicaPush = false,
        bool requestLeaseHeld = false)
    {
        if (!requestLeaseHeld)
            EnterRequest();
        try
        {
            using var timeout = CreateTimeout(commandTimeout, cancellationToken);
            var effectiveCancellationToken = timeout?.Token ?? cancellationToken;

            var json = JsonSerializer.Serialize(
                request,
                AhtolaRemoteJsonContext.Default.RemotePipelineRequest);
            using var response = await SendProtocolRequestAsync(
                    _pipelineUri,
                    json,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            var body = await ReadResponseBodyAsync(response, effectiveCancellationToken).ConfigureAwait(false);
            if (ShouldFallbackToV2(response, request.Baton))
            {
                _protocolVersion = RemoteProtocolVersion.V2;
                _pipelineUri = CreateProtocolUri(_pipelineUri, "/v2/pipeline");
                using var fallbackResponse = await SendProtocolRequestAsync(
                        _pipelineUri,
                        json,
                        effectiveCancellationToken)
                    .ConfigureAwait(false);
                var fallbackBody = await ReadResponseBodyAsync(fallbackResponse, effectiveCancellationToken).ConfigureAwait(false);
                return ParsePipelineResponse(fallbackResponse, fallbackBody, replicaPush);
            }

            if (_protocolVersion == RemoteProtocolVersion.Unknown)
                _protocolVersion = RemoteProtocolVersion.V3;
            return ParsePipelineResponse(response, body, replicaPush);
        }
        finally
        {
            if (!requestLeaseHeld)
                ExitRequest();
        }
    }

    private async Task<RemoteCursor?> SendCursorAsync(
        RemoteCursorRequest request,
        int commandTimeout,
        bool closeAfter,
        CancellationToken cancellationToken,
        Action<Exception>? failureCallback)
    {
        EnterRequest();
        var cursorOwnsRequest = false;
        using var timeout = CreateTimeout(commandTimeout, cancellationToken);
        var effectiveCancellationToken = timeout?.Token ?? cancellationToken;
        var json = JsonSerializer.Serialize(
            request,
            AhtolaRemoteJsonContext.Default.RemoteCursorRequest);

        HttpResponseMessage? response = null;
        try
        {
            response = await SendProtocolRequestAsync(
                    _cursorUri,
                    json,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            if (ShouldFallbackToV2(response, request.Baton))
            {
                response.Dispose();
                response = null;
                _protocolVersion = RemoteProtocolVersion.V2;
                _pipelineUri = CreateProtocolUri(_pipelineUri, "/v2/pipeline");
                return null;
            }

            if (_protocolVersion == RemoteProtocolVersion.Unknown)
                _protocolVersion = RemoteProtocolVersion.V3;
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadResponseBodyAsync(response, effectiveCancellationToken).ConfigureAwait(false);
                throw CreateHttpException(response, body, replicaPush: false);
            }

            var stream = await response.Content.ReadAsStreamAsync(effectiveCancellationToken).ConfigureAwait(false);
            var cursor = new RemoteCursor(
                this,
                response,
                stream,
                commandTimeout,
                closeAfter,
                failureCallback);
            cursorOwnsRequest = true;
            response = null;
            try
            {
                await cursor.InitializeAsync(effectiveCancellationToken).ConfigureAwait(false);
                return cursor;
            }
            catch
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally
        {
            if (!cursorOwnsRequest)
                ExitRequest();
        }
    }

    private async Task<HttpResponseMessage> SendProtocolRequestAsync(
        Uri requestUri,
        string json,
        CancellationToken cancellationToken)
        => await AhtolaRemoteTransportSecurity
            .SendAsync(
                _httpClient,
                requestUri,
                uri => CreateProtocolHttpRequest(uri, json),
                _authToken,
                remoteEncryptionConfigured: _remoteEncryptionKey is not null,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<string> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        => await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    private RemotePipelineResponse ParsePipelineResponse(
        HttpResponseMessage response,
        string body,
        bool replicaPush)
    {
        if (!response.IsSuccessStatusCode)
            throw CreateHttpException(response, body, replicaPush);

        try
        {
            return JsonSerializer.Deserialize(
                       body,
                       AhtolaRemoteJsonContext.Default.RemotePipelineResponse)
                   ?? throw new AhtolaException("Remote request returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new AhtolaException($"Unable to parse remote response: {ex.Message}");
        }
    }

    private static AhtolaException CreateHttpException(
        HttpResponseMessage response,
        string body,
        bool replicaPush)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new AhtolaReplicaConflictException(
                $"Remote replica push conflicted with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return new AhtolaException(
            $"Remote request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            response.StatusCode,
            replicaPush);
    }

    private bool ShouldFallbackToV2(HttpResponseMessage response, string? requestBaton)
        => _protocolVersion == RemoteProtocolVersion.Unknown
           && _allowV2Fallback
           && requestBaton is null
           && response.StatusCode == HttpStatusCode.NotFound;

    private static CancellationTokenSource? CreateTimeout(int commandTimeout, CancellationToken cancellationToken)
    {
        if (commandTimeout <= 0 && !cancellationToken.CanBeCanceled)
            return null;

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (commandTimeout > 0)
            timeout.CancelAfter(TimeSpan.FromSeconds(commandTimeout));
        return timeout;
    }

    private HttpRequestMessage CreateProtocolHttpRequest(Uri requestUri, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (_authToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        if (_remoteEncryptionKey is not null)
            request.Headers.TryAddWithoutValidation(EncryptionKeyHeaderName, _remoteEncryptionKey);
        return request;
    }

    internal static T DeserializeRemoteResult<T>(JsonElement result)
    {
        try
        {
            return result.Deserialize<T>(AhtolaRemoteJsonContext.Default.Options)
                   ?? throw new AhtolaException("Remote response returned an empty result.");
        }
        catch (JsonException ex)
        {
            throw new AhtolaException($"Unable to parse remote response: {ex.Message}");
        }
    }

    private static RemoteStatement BuildStatement(string sql, AhtolaParameterCollection parameters, bool wantRows)
    {
        var bindings = AhtolaParameterBindings.Create(sql, parameters);
        var statement = new RemoteStatement
        {
            Sql = sql,
            WantRows = wantRows,
        };

        for (var index = 1; index <= bindings.Map.Count; index++)
        {
            if (!bindings.Map.IsReferenced(index))
                continue;

            var parameter = bindings.GetParameter(index);
            var value = RemoteRequestValue.FromAhtolaValue(parameter.ToValue());
            var sqlName = bindings.Map.GetName(index);
            if (sqlName is null)
            {
                statement.Args.Add(value);
            }
            else
            {
                statement.NamedArgs.Add(new RemoteNamedArg
                {
                    Name = sqlName,
                    Value = value,
                });
            }
        }

        return statement;
    }

    internal static void ValidateParameters(string sql, AhtolaParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var bindings = AhtolaParameterBindings.Create(sql, parameters);
        for (var index = 1; index <= bindings.Map.Count; index++)
        {
            if (!bindings.Map.IsReferenced(index))
                continue;

            var value = bindings.GetParameter(index).ToValue();
            if (value.ValueType == AhtolaValueType.Real && !double.IsFinite(value.RealValue))
            {
                throw new AhtolaParameterException(
                    "Only finite numbers (not Infinity or NaN) can be passed as remote arguments.");
            }
        }
    }

    internal static void ValidateParameters(IReadOnlyList<AhtolaBatchCommand> commands)
    {
        foreach (var command in commands)
            ValidateParameters(command.CommandText, command.Parameters);
    }

    private static RemoteBatchCondition BuildCondition(AhtolaRemoteBatchCondition condition)
    {
        var remote = new RemoteBatchCondition
        {
            Type = condition.Type,
            Step = condition.Step,
            Condition = condition.Operand is null ? null : BuildCondition(condition.Operand),
        };
        if (condition.Operands is not null)
        {
            remote.Conditions = new List<RemoteBatchCondition>(condition.Operands.Count);
            foreach (var operand in condition.Operands)
                remote.Conditions.Add(BuildCondition(operand));
        }

        return remote;
    }

    private static RemoteStatementResult ExtractExecuteResult(RemotePipelineResponse response)
    {
        if (response.Results.Count == 0)
            throw new AhtolaException("Remote request returned no results.");

        var result = response.Results[0];
        RemoteStatementResult statementResult;
        switch (result.Type)
        {
            case "ok":
                if (result.Response is null)
                    throw new AhtolaException("Remote request returned an empty ok response.");
                if (result.Response.Type != "execute")
                    throw new AhtolaException($"Remote request returned unexpected response type: {result.Response.Type}");
                statementResult = result.Response.DeserializeResult<RemoteStatementResult>();
                break;

            case "error":
                throw CreateRemoteError(result.Error);

            default:
                throw new AhtolaException($"Remote request returned unexpected result type: {result.Type}");
        }

        ValidateOptionalTrailingClose(response, "Remote request");
        return statementResult;
    }

    private IReadOnlyList<RemoteStatementResult> ExtractBatchResults(
        RemotePipelineResponse response,
        int expectedCount,
        Action<int>? stepSucceeded,
        bool replicaPush = false,
        IReadOnlyDictionary<int, ReplicaPushConflictContext>? replayedChangeContexts = null)
    {
        if (response.Results.Count == 0)
            throw new AhtolaException("Remote batch returned no results.");

        var result = response.Results[0];
        List<RemoteStatementResult> statementResults;
        switch (result.Type)
        {
            case "ok":
                if (result.Response is null)
                    throw new AhtolaException("Remote batch returned an empty ok response.");
                if (result.Response.Type != "batch")
                    throw new AhtolaException($"Remote batch returned unexpected response type: {result.Response.Type}");

                var batch = result.Response.DeserializeResult<RemoteBatchResult>();
                UpdateReplicationIndex(batch.ReplicationIndex);
                foreach (var statementResult in batch.StepResults)
                {
                    if (statementResult is not null)
                        UpdateReplicationIndex(statementResult.ReplicationIndex);
                }

                statementResults = ExtractBatchStepResults(
                    batch,
                    expectedCount,
                    stepSucceeded,
                    replicaPush,
                    replayedChangeContexts);
                break;

            case "error":
                throw CreateRemoteError(result.Error, replicaPush);

            default:
                throw new AhtolaException($"Remote batch returned unexpected result type: {result.Type}");
        }

        ValidateOptionalTrailingClose(response, "Remote batch");
        return statementResults;
    }

    private void UpdateReplicationIndex(JsonElement encodedIndex)
    {
        if (encodedIndex.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        ulong index;
        if (encodedIndex.ValueKind == JsonValueKind.String)
        {
            if (!ulong.TryParse(
                encodedIndex.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index))
            {
                throw new AhtolaException("Remote response returned an invalid replication_index.");
            }
        }
        else if (encodedIndex.ValueKind == JsonValueKind.Number)
        {
            if (!encodedIndex.TryGetUInt64(out index))
                throw new AhtolaException("Remote response returned an invalid replication_index.");
        }
        else
        {
            throw new AhtolaException("Remote response returned an invalid replication_index.");
        }

        if (_replicationIndex is null || index > _replicationIndex.Value)
            _replicationIndex = index;
    }

    private static void ValidateOptionalTrailingClose(RemotePipelineResponse response, string operation)
    {
        if (response.Results.Count == 1)
            return;
        if (response.Results.Count > 2)
            throw new AhtolaException($"{operation} returned too many results.");

        var result = response.Results[1];
        switch (result.Type)
        {
            case "ok":
                if (result.Response?.Type != "close")
                    throw new AhtolaException($"{operation} returned unexpected response type: {result.Response?.Type}");
                break;

            case "error":
                break;

            default:
                throw new AhtolaException($"{operation} returned unexpected result type: {result.Type}");
        }
    }

    private static List<RemoteStatementResult> ExtractBatchStepResults(
        RemoteBatchResult batch,
        int expectedCount,
        Action<int>? stepSucceeded,
        bool replicaPush,
        IReadOnlyDictionary<int, ReplicaPushConflictContext>? replayedChangeContexts)
    {
        if (batch.StepErrors.Count != expectedCount || batch.StepResults.Count != expectedCount)
        {
            throw new AhtolaException(
                $"Remote batch returned an unexpected result shape: {batch.StepResults.Count} results, {batch.StepErrors.Count} errors, expected {expectedCount}.",
                replicaPush ? AhtolaReplicaPushFailureKind.InvalidLocalState : (AhtolaReplicaPushFailureKind?)null);
        }

        for (var i = 0; i < batch.StepErrors.Count; i++)
        {
            if (batch.StepErrors[i] is null && batch.StepResults[i] is not null)
                stepSucceeded?.Invoke(i);
        }

        for (var i = 0; i < batch.StepErrors.Count; i++)
        {
            if (batch.StepErrors[i] is { } error)
            {
                var context = replayedChangeContexts is not null
                    && replayedChangeContexts.TryGetValue(i, out var value)
                    ? value
                    : default;
                throw CreateRemoteError(
                    error,
                    replicaPush,
                    context.Kind,
                    context.LocalChangeSequence);
            }
        }

        var statementResults = new List<RemoteStatementResult>(expectedCount);
        for (var i = 0; i < batch.StepResults.Count; i++)
        {
            // BatchCond-skipped steps return null in both step arrays. Keep a zero-row placeholder
            // so results remain aligned with the caller's command indexes.
            statementResults.Add(batch.StepResults[i] ?? RemoteStatementResult.Skipped());
        }

        return statementResults;
    }

    private static void ValidateCloseResult(RemotePipelineResponse response)
    {
        foreach (var result in response.Results)
        {
            switch (result.Type)
            {
                case "ok":
                    if (result.Response?.Type is not "close")
                        throw new AhtolaException($"Remote close returned unexpected response type: {result.Response?.Type}");
                    break;

                case "error":
                    throw CreateRemoteError(result.Error);

                default:
                    throw new AhtolaException($"Remote close returned unexpected result type: {result.Type}");
            }
        }
    }

    private static AhtolaException CreateRemoteError(
        RemoteError? error,
        bool replicaPush = false,
        AhtolaReplicaConflictKind conflictKind = AhtolaReplicaConflictKind.Unknown,
        long? localChangeSequence = null)
    {
        var invalidLocalState = replicaPush ? AhtolaReplicaPushFailureKind.InvalidLocalState : (AhtolaReplicaPushFailureKind?)null;
        if (error is null)
            return new AhtolaRemoteSqlException("Remote SQL execution failed.", null, null, invalidLocalState);

        if (replicaPush && IsConflict(error))
        {
            return new AhtolaReplicaConflictException(
                $"Remote replica push conflicted: {error.Message}",
                error.Code,
                conflictKind,
                localChangeSequence);
        }

        var message = string.IsNullOrWhiteSpace(error.Code)
            ? $"Remote SQL execution failed: {error.Message}"
            : $"Remote SQL execution failed: {error.Message} ({error.Code})";
        return new AhtolaRemoteSqlException(message, error.Code, error.Message, invalidLocalState);
    }

    private static bool IsConflict(RemoteError error)
        => error.Code?.Contains("CONSTRAINT", StringComparison.OrdinalIgnoreCase) == true
           || error.Code?.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) == true
           || error.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase);

    private readonly record struct ReplicaPushConflictContext(
        AhtolaReplicaConflictKind Kind,
        long? LocalChangeSequence);

    private void UpdateSession(RemotePipelineResponse response, bool closeAfter)
    {
        UpdateBaseUrl(response.BaseUrl);

        _baton = closeAfter ? null : response.Baton;
    }

    private void UpdateCursorSession(RemoteCursorHeader header)
    {
        UpdateBaseUrl(header.BaseUrl);
        if (string.IsNullOrWhiteSpace(header.Baton))
            throw new AhtolaException("Remote cursor response did not include a baton.");
        _baton = header.Baton;
    }

    private void UpdateBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        var endpoint = new Uri(_pipelineUri, baseUrl);
        var pipelinePath = _protocolVersion == RemoteProtocolVersion.V2
            ? "/v2/pipeline"
            : "/v3/pipeline";
        var pipelineUri = CreateProtocolUri(endpoint, pipelinePath);
        var cursorUri = CreateProtocolUri(endpoint, "/v3/cursor");
        AhtolaRemoteTransportSecurity.ValidateRedirectOrigin(
            _pipelineUri,
            pipelineUri,
            credentialsConfigured: _authToken is not null || _remoteEncryptionKey is not null);
        AhtolaRemoteTransportSecurity.Validate(
            pipelineUri,
            _authToken,
            remoteEncryptionConfigured: _remoteEncryptionKey is not null);
        _pipelineUri = pipelineUri;
        _cursorUri = cursorUri;
    }

    private async ValueTask FinishCursorAsync(
        bool successful,
        bool closeAfter,
        int commandTimeout)
    {
        try
        {
            if ((closeAfter || !successful) && _baton is not null)
            {
                var closeTimeout = successful
                    ? commandTimeout
                    : commandTimeout <= 0
                        ? 1
                        : Math.Min(commandTimeout, 1);
                try
                {
                    await CloseCursorSessionAsync(closeTimeout).ConfigureAwait(false);
                }
                catch
                {
                    ResetSession();
                }
            }
        }
        finally
        {
            ExitRequest();
        }

        if (!successful)
            ResetSession();
    }

    private async Task CloseCursorSessionAsync(int commandTimeout)
    {
        if (_baton is null)
            return;

        var request = new RemotePipelineRequest
        {
            Baton = _baton,
            Requests = [RemoteStreamRequest.Close()],
        };
        var response = await SendPipelineAsync(
                request,
                commandTimeout,
                CancellationToken.None,
                requestLeaseHeld: true)
            .ConfigureAwait(false);
        UpdateSession(response, closeAfter: false);
        ValidateCloseResult(response);
        _baton = null;
    }

    private void EnterRequest()
    {
        lock (_requestGate)
        {
            if (_requestInFlight)
            {
                throw new InvalidOperationException(
                    "A remote operation is already in progress on this connection. Complete or dispose the active reader first.");
            }
            _requestInFlight = true;
        }
    }

    private void ExitRequest()
    {
        lock (_requestGate)
            _requestInFlight = false;
    }

    private static (RemoteProtocolVersion Version, bool AllowV2Fallback) DetectProtocol(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v2/pipeline", StringComparison.OrdinalIgnoreCase))
            return (RemoteProtocolVersion.V2, false);
        if (path.EndsWith("/v3/pipeline", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/v3/cursor", StringComparison.OrdinalIgnoreCase))
        {
            return (RemoteProtocolVersion.V3, false);
        }

        return (RemoteProtocolVersion.Unknown, true);
    }

    private static Uri CreateProtocolUri(Uri endpoint, string protocolPath)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        var path = builder.Path.TrimEnd('/');
        foreach (var suffix in new[] { "/v2/pipeline", "/v3/pipeline", "/v3/cursor" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }

        builder.Path = string.IsNullOrEmpty(path)
            ? protocolPath
            : path + protocolPath;

        return builder.Uri;
    }

    private enum RemoteProtocolVersion
    {
        Unknown,
        V2,
        V3,
    }

    private sealed class RemotePipelineRequest
    {
        [JsonPropertyName("baton")]
        public string? Baton { get; init; }

        [JsonPropertyName("requests")]
        public List<RemoteStreamRequest> Requests { get; init; } = [];
    }

    private sealed class RemoteCursorRequest
    {
        [JsonPropertyName("baton")]
        public string? Baton { get; init; }

        [JsonPropertyName("batch")]
        public RemoteBatch Batch { get; init; } = new();
    }

    private sealed class RemoteStreamRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "";

        [JsonPropertyName("stmt")]
        public RemoteStatement? Statement { get; init; }

        [JsonPropertyName("batch")]
        public RemoteBatch? BatchRequest { get; init; }

        public static RemoteStreamRequest Execute(RemoteStatement statement)
        {
            return new RemoteStreamRequest
            {
                Type = "execute",
                Statement = statement,
            };
        }

        public static RemoteStreamRequest Batch(RemoteBatch batch)
        {
            return new RemoteStreamRequest
            {
                Type = "batch",
                BatchRequest = batch,
            };
        }

        public static RemoteStreamRequest Close()
        {
            return new RemoteStreamRequest
            {
                Type = "close",
            };
        }
    }

    private sealed class RemoteStatement
    {
        [JsonPropertyName("sql")]
        public string Sql { get; init; } = "";

        [JsonPropertyName("args")]
        public List<RemoteRequestValue> Args { get; } = [];

        [JsonPropertyName("named_args")]
        public List<RemoteNamedArg> NamedArgs { get; } = [];

        [JsonPropertyName("want_rows")]
        public bool WantRows { get; init; }
    }

    private sealed class RemoteBatch
    {
        [JsonPropertyName("steps")]
        public List<RemoteBatchStep> Steps { get; init; } = [];

        [JsonPropertyName("replication_index")]
        public string? ReplicationIndex { get; init; }
    }

    private sealed class RemoteBatchStep
    {
        [JsonPropertyName("condition")]
        public RemoteBatchCondition? Condition { get; init; }

        [JsonPropertyName("stmt")]
        public RemoteStatement Statement { get; init; } = new();
    }

    private sealed class RemoteBatchCondition
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "";

        [JsonPropertyName("step")]
        public int? Step { get; init; }

        [JsonPropertyName("cond")]
        public RemoteBatchCondition? Condition { get; init; }

        [JsonPropertyName("conds")]
        public List<RemoteBatchCondition>? Conditions { get; set; }
    }

    private sealed class RemoteNamedArg
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("value")]
        public RemoteRequestValue Value { get; init; } = RemoteRequestValue.Null();
    }

    [JsonConverter(typeof(RemoteRequestValueJsonConverter))]
    private sealed class RemoteRequestValue
    {
        public string Type { get; init; } = "";

        public string? StringValue { get; init; }

        public string? Base64 { get; init; }

        public static RemoteRequestValue Null()
        {
            return new RemoteRequestValue
            {
                Type = "null",
            };
        }

        public static RemoteRequestValue FromAhtolaValue(AhtolaValue value)
        {
            return value.ValueType switch
            {
                AhtolaValueType.Empty or AhtolaValueType.Null => Null(),
                AhtolaValueType.Integer => new RemoteRequestValue
                {
                    Type = "integer",
                    StringValue = value.IntValue.ToString(CultureInfo.InvariantCulture),
                },
                AhtolaValueType.Real => new RemoteRequestValue
                {
                    Type = "float",
                    FloatValue = value.RealValue,
                },
                AhtolaValueType.Text => new RemoteRequestValue
                {
                    Type = "text",
                    StringValue = value.StringValue ?? string.Empty,
                },
                AhtolaValueType.Blob => new RemoteRequestValue
                {
                    Type = "blob",
                    Base64 = Convert.ToBase64String(value.BlobValue ?? []),
                },
                _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueType, null),
            };
        }

        public double? FloatValue { get; init; }
    }

    private sealed class RemoteRequestValueJsonConverter : JsonConverter<RemoteRequestValue>
    {
        public override RemoteRequestValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("Remote request values are serialized only.");

        public override void Write(Utf8JsonWriter writer, RemoteRequestValue value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.Type);
            if (value.FloatValue is { } floatValue)
                writer.WriteNumber("value", floatValue);
            else if (value.StringValue is not null)
                writer.WriteString("value", value.StringValue);
            else if (value.Base64 is not null)
                writer.WriteString("base64", value.Base64);
            writer.WriteEndObject();
        }
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(RemotePipelineRequest))]
    [JsonSerializable(typeof(RemoteCursorRequest))]
    [JsonSerializable(typeof(RemotePipelineResponse))]
    [JsonSerializable(typeof(RemoteCursorHeader))]
    [JsonSerializable(typeof(RemoteCursorEntry))]
    [JsonSerializable(typeof(RemoteBatchResult))]
    [JsonSerializable(typeof(RemoteStatementResult))]
    [JsonSerializable(typeof(RemoteRequestValue))]
    private sealed partial class AhtolaRemoteJsonContext : JsonSerializerContext;

    internal sealed class RemoteCursor : IAsyncDisposable
    {
        private const int MaximumTypeInferenceLookaheadRows = 64;

        private readonly AhtolaRemoteClient _owner;
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;
        private readonly int _commandTimeout;
        private readonly bool _closeAfter;
        private readonly Action<Exception>? _failureCallback;
        private readonly Queue<List<RemoteResponseValue>> _bufferedRows = new();
        private readonly HashSet<int> _exhaustedTypeInferenceOrdinals = [];
        private List<RemoteResponseValue>? _pendingRow;
        private bool _stepOpen;
        private bool _terminated;
        private bool _ownerFinished;
        private bool _disposed;
        private bool _failureReported;

        public RemoteCursor(
            AhtolaRemoteClient owner,
            HttpResponseMessage response,
            Stream stream,
            int commandTimeout,
            bool closeAfter,
            Action<Exception>? failureCallback)
        {
            _owner = owner;
            _response = response;
            _reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);
            _commandTimeout = commandTimeout;
            _closeAfter = closeAfter;
            _failureCallback = failureCallback;
        }

        public List<RemoteColumn> Columns { get; private set; } = [];

        public int RecordsAffected { get; private set; }

        public RemoteResponseValue? FindFirstNonNullValue(
            int ordinal,
            CancellationToken cancellationToken)
        {
            var collected = new List<List<RemoteResponseValue>>();
            while (_bufferedRows.TryDequeue(out var buffered))
                collected.Add(buffered);
            if (_pendingRow is not null)
            {
                collected.Add(_pendingRow);
                _pendingRow = null;
            }

            try
            {
                foreach (var row in collected)
                {
                    if (ordinal < row.Count && row[ordinal].Type != "null")
                        return row[ordinal];
                }

                if (_exhaustedTypeInferenceOrdinals.Contains(ordinal))
                    return null;

                var rowsRead = 0;
                while (!_terminated && rowsRead < MaximumTypeInferenceLookaheadRows)
                {
                    var row = ReadRowAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                    if (row is null)
                        break;
                    collected.Add(row);
                    rowsRead++;
                    if (ordinal < row.Count && row[ordinal].Type != "null")
                        return row[ordinal];
                }
                if (!_terminated && rowsRead == MaximumTypeInferenceLookaheadRows)
                    _exhaustedTypeInferenceOrdinals.Add(ordinal);
                return null;
            }
            finally
            {
                foreach (var row in collected)
                    _bufferedRows.Enqueue(row);
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var headerLine = await ReadRequiredLineAsync(cancellationToken).ConfigureAwait(false);
                RemoteCursorHeader header;
                try
                {
                    header = JsonSerializer.Deserialize(
                                 headerLine,
                                 AhtolaRemoteJsonContext.Default.RemoteCursorHeader)
                             ?? throw new AhtolaException("Remote cursor returned an empty header.");
                }
                catch (JsonException exception)
                {
                    throw Malformed(exception.Message);
                }
                _owner.UpdateCursorSession(header);

                while (true)
                {
                    var entry = await ReadEntryAsync(cancellationToken).ConfigureAwait(false);
                    switch (entry.Type)
                    {
                        case "step_begin":
                            if (entry.Step != 0)
                                throw Malformed($"expected step 0 but received step {entry.Step?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
                            _stepOpen = true;
                            Columns = entry.Columns
                                ?? throw Malformed("step_begin did not include cols");
                            return;

                        case "step_error":
                            var stepError = CreateRemoteError(entry.Error);
                            await CompleteAfterStepErrorAsync(stepError, cancellationToken).ConfigureAwait(false);
                            throw stepError;

                        case "error":
                            throw CreateRemoteError(entry.Error);

                        case "replication_index":
                            throw Malformed("cursor completed before step_begin");

                        case "row":
                        case "step_end":
                            throw Malformed($"{entry.Type} was received before step_begin");

                        default:
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_terminated)
                    await HandleFailureAsync(exception).ConfigureAwait(false);
                throw;
            }
        }

        public bool EnsureHasRows(CancellationToken cancellationToken)
            => EnsureHasRowsAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

        public async ValueTask<bool> EnsureHasRowsAsync(CancellationToken cancellationToken)
        {
            if (_pendingRow is not null || _bufferedRows.Count > 0)
                return true;

            _pendingRow = await ReadRowAsync(cancellationToken).ConfigureAwait(false);
            return _pendingRow is not null;
        }

        public List<RemoteResponseValue>? ReadRow(CancellationToken cancellationToken)
            => ReadRowAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

        public async ValueTask<List<RemoteResponseValue>?> ReadRowAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bufferedRows.TryDequeue(out var buffered))
                return buffered;
            if (_pendingRow is not null)
            {
                var pending = _pendingRow;
                _pendingRow = null;
                return pending;
            }
            if (_terminated)
                return null;

            using var readCancellation = CreateReadCancellation(cancellationToken);
            var effectiveCancellationToken = readCancellation?.Token ?? cancellationToken;
            try
            {
                while (true)
                {
                    var entry = await ReadEntryAsync(effectiveCancellationToken).ConfigureAwait(false);
                    switch (entry.Type)
                    {
                        case "row":
                            if (!_stepOpen)
                                throw Malformed("row was received outside a step");
                            var row = entry.Row ?? throw Malformed("row entry did not include row");
                            if (row.Count != Columns.Count)
                            {
                                throw Malformed(
                                    $"row contained {row.Count} values for {Columns.Count} columns");
                            }
                            return row;

                        case "step_end":
                            if (!_stepOpen)
                                throw Malformed("step_end was received outside a step");
                            _stepOpen = false;
                            RecordsAffected = checked((int)entry.AffectedRowCount);
                            break;

                        case "step_error":
                            _stepOpen = false;
                            var stepError = CreateRemoteError(entry.Error);
                            await CompleteAfterStepErrorAsync(stepError, effectiveCancellationToken).ConfigureAwait(false);
                            throw stepError;

                        case "error":
                            throw CreateRemoteError(entry.Error);

                        case "replication_index":
                            if (_stepOpen)
                                throw Malformed("cursor terminated before step_end");
                            _terminated = true;
                            await FinishOwnerAsync(successful: true).ConfigureAwait(false);
                            return null;

                        case "step_begin":
                            throw Malformed("cursor returned more than one step");

                        default:
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_terminated)
                    await HandleFailureAsync(exception).ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!_terminated)
                {
                    if (await ReadRowAsync(cleanup.Token).ConfigureAwait(false) is null)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _disposed = true;
                _reader.Dispose();
                _response.Dispose();
                if (!_ownerFinished)
                    await FinishOwnerAsync(successful: _terminated).ConfigureAwait(false);
            }
        }

        private CancellationTokenSource? CreateReadCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled && _commandTimeout <= 0)
                return null;

            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_commandTimeout > 0)
                timeout.CancelAfter(TimeSpan.FromSeconds(_commandTimeout));
            return timeout;
        }

        private async Task<RemoteCursorEntry> ReadEntryAsync(CancellationToken cancellationToken)
        {
            var line = await ReadRequiredLineAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = JsonSerializer.Deserialize(
                    line,
                    AhtolaRemoteJsonContext.Default.RemoteCursorEntry);
                if (entry is null || string.IsNullOrWhiteSpace(entry.Type))
                    throw Malformed("entry did not include type");
                return entry;
            }
            catch (JsonException exception)
            {
                throw Malformed(exception.Message);
            }
        }

        private async Task CompleteAfterStepErrorAsync(
            AhtolaException stepError,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var entry = await ReadEntryAsync(cancellationToken).ConfigureAwait(false);
                switch (entry.Type)
                {
                    case "replication_index":
                        _terminated = true;
                        await FinishOwnerAsync(successful: true).ConfigureAwait(false);
                        return;

                    case "error":
                        throw CreateRemoteError(entry.Error);

                    case "step_begin":
                    case "row":
                    case "step_end":
                    case "step_error":
                        throw Malformed($"unexpected {entry.Type} after step_error");

                    default:
                        break;
                }
            }
        }

        private async Task<string> ReadRequiredLineAsync(CancellationToken cancellationToken)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw Malformed("response ended before the replication_index terminator");
            if (string.IsNullOrWhiteSpace(line))
                throw Malformed("response contained an empty frame");
            return line;
        }

        private async Task HandleFailureAsync(Exception failure)
        {
            if (_disposed)
                return;

            _disposed = true;
            _reader.Dispose();
            _response.Dispose();
            await FinishOwnerAsync(successful: false).ConfigureAwait(false);
            if (!_failureReported)
            {
                _failureReported = true;
                _failureCallback?.Invoke(failure);
            }
        }

        private async ValueTask FinishOwnerAsync(bool successful)
        {
            if (_ownerFinished)
                return;
            _ownerFinished = true;
            await _owner
                .FinishCursorAsync(successful, _closeAfter, _commandTimeout)
                .ConfigureAwait(false);
        }

        private static AhtolaException Malformed(string detail)
            => new($"Unable to parse remote cursor response: {detail}.");
    }
}

internal sealed class RemoteReaderExecution
{
    private RemoteReaderExecution(
        RemoteStatementResult? bufferedResult,
        AhtolaRemoteClient.RemoteCursor? cursor,
        AhtolaSchemaCollections.ReaderSchemaSource? schemaSource = null)
    {
        BufferedResult = bufferedResult;
        Cursor = cursor;
        SchemaSource = schemaSource;
    }

    public RemoteStatementResult? BufferedResult { get; }

    public AhtolaRemoteClient.RemoteCursor? Cursor { get; }

    public AhtolaSchemaCollections.ReaderSchemaSource? SchemaSource { get; }

    public static RemoteReaderExecution FromBuffered(RemoteStatementResult result)
        => new(result, null);

    public static RemoteReaderExecution FromCursor(AhtolaRemoteClient.RemoteCursor cursor)
        => new(null, cursor);

    public RemoteReaderExecution WithSchemaSource(AhtolaSchemaCollections.ReaderSchemaSource? schemaSource)
        => new(BufferedResult, Cursor, schemaSource);
}

internal sealed class RemoteCursorHeader
{
    [JsonPropertyName("baton")]
    public string? Baton { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }
}

internal sealed class RemoteCursorEntry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("step")]
    public int? Step { get; init; }

    [JsonPropertyName("cols")]
    public List<RemoteColumn>? Columns { get; init; }

    [JsonPropertyName("row")]
    public List<RemoteResponseValue>? Row { get; init; }

    [JsonPropertyName("affected_row_count")]
    public ulong AffectedRowCount { get; init; }

    [JsonPropertyName("last_insert_rowid")]
    public JsonElement LastInsertRowId { get; init; }

    [JsonPropertyName("error")]
    public RemoteError? Error { get; init; }
}

internal sealed class RemotePipelineResponse
{
    [JsonPropertyName("baton")]
    public string? Baton { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("results")]
    public List<RemoteStreamResult> Results { get; init; } = [];
}

internal sealed class RemoteStreamResult
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("response")]
    public RemoteStreamResponse? Response { get; init; }

    [JsonPropertyName("error")]
    public RemoteError? Error { get; init; }
}

internal sealed class RemoteStreamResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    public T DeserializeResult<T>()
    {
        if (Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new AhtolaException($"Remote response {Type} did not include a result.");

        return AhtolaRemoteClient.DeserializeRemoteResult<T>(Result);
    }
}

internal sealed class RemoteError
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("code")]
    public string? Code { get; init; }
}

internal sealed class AhtolaRemoteSqlException : AhtolaException
{
    public AhtolaRemoteSqlException(
        string message,
        string? remoteErrorCode,
        string? remoteErrorMessage,
        AhtolaReplicaPushFailureKind? replicaPushFailureKind = null)
        : base(message, replicaPushFailureKind)
    {
        RemoteErrorCode = remoteErrorCode;
        RemoteErrorMessage = remoteErrorMessage;
    }

    public string? RemoteErrorCode { get; }

    public string? RemoteErrorMessage { get; }

    public bool IsStreamExpired
        => RemoteErrorCode is not null
               && (RemoteErrorCode.Equals("STREAM_EXPIRED", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorCode.Equals("STREAM_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorCode.Equals("BATON_EXPIRED", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorCode.Equals("HRANA_STREAM_EXPIRED", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorCode.Equals("HRANA_STREAM_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorCode.Equals("BA_STREAM_EXPIRED", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorCode.Equals("BA_STREAM_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
           || RemoteErrorMessage is not null
               && (RemoteErrorMessage.Contains("stream expired", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorMessage.Contains("stream has expired", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorMessage.Contains("stream not found", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorMessage.Contains("baton expired", StringComparison.OrdinalIgnoreCase)
                   || RemoteErrorMessage.Contains("baton has expired", StringComparison.OrdinalIgnoreCase));
}

internal sealed class RemoteBatchResult
{
    [JsonPropertyName("step_results")]
    public List<RemoteStatementResult?> StepResults { get; init; } = [];

    [JsonPropertyName("step_errors")]
    public List<RemoteError?> StepErrors { get; init; } = [];

    [JsonPropertyName("replication_index")]
    public JsonElement ReplicationIndex { get; init; }
}

internal sealed class RemoteStatementResult
{
    public static RemoteStatementResult Skipped() => new();

    [JsonPropertyName("cols")]
    public List<RemoteColumn> Columns { get; init; } = [];

    [JsonPropertyName("rows")]
    public List<List<RemoteResponseValue>> Rows { get; init; } = [];

    [JsonPropertyName("affected_row_count")]
    public ulong AffectedRowCount { get; init; }

    [JsonPropertyName("last_insert_rowid")]
    public JsonElement LastInsertRowId { get; init; }

    [JsonPropertyName("replication_index")]
    public JsonElement ReplicationIndex { get; init; }
}

internal sealed class RemoteColumn
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("decltype")]
    public string? DeclType { get; init; }
}

internal sealed class RemoteResponseValue
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }

    [JsonPropertyName("base64")]
    public string? Base64 { get; init; }

    public object ToClrValue()
    {
        return Type switch
        {
            "null" => DBNull.Value,
            "integer" => ParseInteger(),
            "float" => ParseFloat(),
            "text" => Value.GetString() ?? string.Empty,
            "blob" => DecodeBase64(Base64 ?? string.Empty),
            _ => throw new AhtolaException($"Remote response returned unsupported value type: {Type}"),
        };
    }

    public long GetInt64()
    {
        return Type switch
        {
            "integer" => ParseInteger(),
            "float" => checked((long)ParseFloat()),
            "text" => long.Parse(Value.GetString() ?? "", CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert remote {Type} value to Int64."),
        };
    }

    public double GetDouble()
    {
        return Type switch
        {
            "float" => ParseFloat(),
            "integer" => ParseInteger(),
            "text" => double.Parse(Value.GetString() ?? "", CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert remote {Type} value to Double."),
        };
    }

    public decimal GetDecimal()
    {
        return Type switch
        {
            // Format through "G15" instead of Convert.ToDecimal(double): .NET 11 changed
            // double-to-decimal conversion to keep the exact binary expansion, while SQLite
            // REAL-to-decimal semantics round to 15 significant digits.
            "float" => decimal.Parse(ParseFloat().ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            "integer" => ParseInteger(),
            "text" => decimal.Parse(Value.GetString() ?? "", CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert remote {Type} value to Decimal."),
        };
    }

    private long ParseInteger()
    {
        return Value.ValueKind == JsonValueKind.String
            ? long.Parse(Value.GetString() ?? "", CultureInfo.InvariantCulture)
            : Value.GetInt64();
    }

    private double ParseFloat()
    {
        return Value.ValueKind == JsonValueKind.String
            ? double.Parse(Value.GetString() ?? "", CultureInfo.InvariantCulture)
            : Value.GetDouble();
    }

    private static byte[] DecodeBase64(string value)
    {
        var padding = value.Length % 4;
        if (padding != 0)
            value = value.PadRight(value.Length + 4 - padding, '=');

        return Convert.FromBase64String(value);
    }
}
