using Ahtola.Core;

namespace Ahtola;

public class AhtolaException : Exception
{
    public AhtolaException(string message) : base(message)
    {
    }

    internal AhtolaException(string message, System.Net.HttpStatusCode remoteStatusCode)
        : base(message)
    {
        RemoteStatusCode = remoteStatusCode;
    }

    internal System.Net.HttpStatusCode? RemoteStatusCode { get; }

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
