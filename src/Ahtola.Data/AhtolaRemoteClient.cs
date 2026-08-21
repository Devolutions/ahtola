using System.Globalization;
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
        _pipelineUri = CreatePipelineUri(endpoint);
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
        bool replicaPush = false)
    {
        using var timeout = CreateTimeout(commandTimeout, cancellationToken);
        var effectiveCancellationToken = timeout?.Token ?? cancellationToken;

        var json = JsonSerializer.Serialize(
            request,
            AhtolaRemoteJsonContext.Default.RemotePipelineRequest);
        using var response = await AhtolaRemoteTransportSecurity
            .SendAsync(
                _httpClient,
                _pipelineUri,
                requestUri => CreatePipelineHttpRequest(requestUri, json),
                _authToken,
                remoteEncryptionConfigured: _remoteEncryptionKey is not null,
                HttpCompletionOption.ResponseHeadersRead,
                effectiveCancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(effectiveCancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                throw new AhtolaReplicaConflictException(
                    $"Remote replica push conflicted with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            throw new AhtolaException(
                $"Remote request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                response.StatusCode,
                replicaPush);
        }

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

    private static CancellationTokenSource? CreateTimeout(int commandTimeout, CancellationToken cancellationToken)
    {
        if (commandTimeout <= 0)
            return null;

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(commandTimeout));
        return timeout;
    }

    private HttpRequestMessage CreatePipelineHttpRequest(Uri requestUri, string json)
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
        if (!string.IsNullOrWhiteSpace(response.BaseUrl))
        {
            var pipelineUri = CreatePipelineUri(new Uri(_pipelineUri, response.BaseUrl));
            AhtolaRemoteTransportSecurity.Validate(
                pipelineUri,
                _authToken,
                remoteEncryptionConfigured: _remoteEncryptionKey is not null);
            _pipelineUri = pipelineUri;
        }

        _baton = closeAfter ? null : response.Baton;
    }

    private static Uri CreatePipelineUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };

        var path = builder.Path;
        builder.Path = string.IsNullOrEmpty(path) || path == "/"
            ? "/v2/pipeline"
            : path.TrimEnd('/').EndsWith("/v2/pipeline", StringComparison.OrdinalIgnoreCase)
                ? path
                : path.TrimEnd('/') + "/v2/pipeline";

        return builder.Uri;
    }

    private sealed class RemotePipelineRequest
    {
        [JsonPropertyName("baton")]
        public string? Baton { get; init; }

        [JsonPropertyName("requests")]
        public List<RemoteStreamRequest> Requests { get; init; } = [];
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
    [JsonSerializable(typeof(RemotePipelineResponse))]
    [JsonSerializable(typeof(RemoteBatchResult))]
    [JsonSerializable(typeof(RemoteStatementResult))]
    [JsonSerializable(typeof(RemoteRequestValue))]
    private sealed partial class AhtolaRemoteJsonContext : JsonSerializerContext;
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
