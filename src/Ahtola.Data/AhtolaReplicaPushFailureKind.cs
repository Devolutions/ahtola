namespace Ahtola;

/// <summary>
/// Classifies why a managed replica's push of durably journaled local changes failed, so a
/// caller can choose a recovery strategy without inspecting exception types or messages.
/// </summary>
public enum AhtolaReplicaPushFailureKind
{
    /// <summary>
    /// The remote diverged from the locally journaled changes (for example a conflicting row or
    /// schema write). The change journal is retained and is never rebased automatically; resolve
    /// by rebasing the local changes against the current remote state or, once available, by
    /// rolling back the offending prefix via the revert WAL.
    /// </summary>
    Conflict,

    /// <summary>
    /// The push failed because of a network or server condition (request timeout, HTTP
    /// 408/429/5xx, or a transport-level failure) that is expected to succeed if retried without
    /// any local or remote state change.
    /// </summary>
    TransientTransport,

    /// <summary>
    /// The push failed for a reason unrelated to remote divergence or transient transport — an
    /// inconsistency in local state (for example bootstrap metadata or the change journal) or an
    /// unexpected/malformed remote response. Retrying without repairing local state, or the
    /// remote endpoint/configuration, is not expected to help.
    /// </summary>
    InvalidLocalState,
}

/// <summary>
/// Classifies exceptions raised by a managed embedded replica's explicit
/// (<see cref="AhtolaConnection.SyncAsync(AhtolaSyncOptions, CancellationToken)"/>) or automatic
/// synchronization into stable <see cref="AhtolaReplicaPushFailureKind"/> recovery buckets.
/// </summary>
public static class AhtolaReplicaPushFailure
{
    /// <summary>
    /// Classifies <paramref name="exception"/> into a <see cref="AhtolaReplicaPushFailureKind"/>.
    /// Exceptions raised at the managed replica push response boundary
    /// (<see cref="AhtolaReplicaConflictException"/> and the internal push-specific
    /// <see cref="AhtolaException"/> instances) are classified using the context captured when
    /// they were thrown. Other exceptions — including framework transport exceptions that never
    /// produced an <see cref="AhtolaException"/> — are classified on a best-effort basis from
    /// their type and any HTTP status code they carry.
    /// </summary>
    public static AhtolaReplicaPushFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            AhtolaReplicaConflictException => AhtolaReplicaPushFailureKind.Conflict,
            AhtolaException { ReplicaPushFailureKind: { } kind } => kind,
            AhtolaException ahtolaException => ahtolaException.IsTransientRemoteHttpFailure
                ? AhtolaReplicaPushFailureKind.TransientTransport
                : AhtolaReplicaPushFailureKind.InvalidLocalState,
            HttpRequestException httpRequestException => IsTransientHttpRequestFailure(httpRequestException)
                ? AhtolaReplicaPushFailureKind.TransientTransport
                : AhtolaReplicaPushFailureKind.InvalidLocalState,
            TaskCanceledException or OperationCanceledException => AhtolaReplicaPushFailureKind.TransientTransport,
            _ => AhtolaReplicaPushFailureKind.InvalidLocalState,
        };
    }

    private static bool IsTransientHttpRequestFailure(HttpRequestException exception)
        => exception.StatusCode is null
               or System.Net.HttpStatusCode.RequestTimeout
               or System.Net.HttpStatusCode.TooManyRequests
           || exception.StatusCode is { } status && (int)status is >= 500 and <= 599;
}
