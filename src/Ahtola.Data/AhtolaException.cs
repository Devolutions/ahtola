using Ahtola.Core;

namespace Ahtola;

public class AhtolaException : Exception
{
    public AhtolaException(string message) : base(message)
    {
    }

    internal AhtolaException(
        string message,
        System.Net.HttpStatusCode remoteStatusCode,
        bool replicaPush = false)
        : base(message)
    {
        RemoteStatusCode = remoteStatusCode;
        if (replicaPush)
        {
            ReplicaPushFailureKind = IsTransientRemoteHttpFailure
                ? AhtolaReplicaPushFailureKind.TransientTransport
                : AhtolaReplicaPushFailureKind.InvalidLocalState;
        }
    }

    internal AhtolaException(string message, AhtolaReplicaPushFailureKind? replicaPushFailureKind)
        : base(message)
    {
        ReplicaPushFailureKind = replicaPushFailureKind;
    }

    internal System.Net.HttpStatusCode? RemoteStatusCode { get; }

    /// <summary>
    /// Classifies this exception at the managed replica push response boundary, when it was
    /// raised while pushing durably journaled local changes. <see langword="null"/> for
    /// exceptions unrelated to a replica push. Use
    /// <see cref="AhtolaReplicaPushFailure.Classify(Exception)"/> for a best-effort
    /// classification of any exception, including one where this property is
    /// <see langword="null"/>.
    /// </summary>
    public AhtolaReplicaPushFailureKind? ReplicaPushFailureKind { get; }

    internal bool IsTransientRemoteHttpFailure
        => RemoteStatusCode is System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            || RemoteStatusCode is { } status
            && (int)status is >= 500 and <= 599;

    internal AhtolaException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal static AhtolaException FromCore(EmbeddedSqlException exception)
        => new(exception.Message, exception);

    internal static AhtolaException FromCorePreparation(EmbeddedSqlException exception)
        => new($"Unable to prepare statement: Parse error: {exception.Message}", exception);
}

/// <summary>
/// Indicates that a parameter cannot be represented by the remote protocol.
/// </summary>
public sealed class AhtolaParameterException(string message) : AhtolaException(message);
