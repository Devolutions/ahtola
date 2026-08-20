using System.Net;
using System.Net.Http;
using Ahtola;

namespace Ahtola.Data.Sqlite;

/// <summary>
/// Classifies a remote failure for retry policies.
/// </summary>
public enum SqliteRemoteErrorClassification
{
    Permanent,
    Transient,
}

/// <summary>
/// A SQLite facade error produced by a direct Hrana request.
/// </summary>
public sealed class SqliteRemoteException : SqliteException
{
    internal SqliteRemoteException(
        string message,
        int errorCode,
        SqliteRemoteErrorClassification classification,
        HttpStatusCode? httpStatusCode)
        : base(message, errorCode)
    {
        Classification = classification;
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>
    /// Gets whether retrying this remote operation may succeed.
    /// </summary>
    public SqliteRemoteErrorClassification Classification { get; }

    /// <summary>
    /// Gets the HTTP status returned by the endpoint, when one was available.
    /// </summary>
    public HttpStatusCode? HttpStatusCode { get; }

    /// <summary>
    /// Gets whether retrying this remote operation may succeed.
    /// </summary>
    public override bool IsTransient => Classification == SqliteRemoteErrorClassification.Transient;
}

/// <summary>
/// Provides stable retry classification for exceptions surfaced by the SQLite facade.
/// </summary>
public static class SqliteRemoteExceptionClassifier
{
    /// <summary>
    /// Classifies a remote facade exception as transient or permanent.
    /// </summary>
    public static SqliteRemoteErrorClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SqliteRemoteException remote => remote.Classification,
            AhtolaException { IsTransientRemoteHttpFailure: true } => SqliteRemoteErrorClassification.Transient,
            HttpRequestException => SqliteRemoteErrorClassification.Transient,
            TimeoutException => SqliteRemoteErrorClassification.Transient,
            _ => SqliteRemoteErrorClassification.Permanent,
        };
    }

    /// <summary>
    /// Returns whether an exception represents a retryable remote failure.
    /// </summary>
    public static bool IsTransient(Exception exception)
        => Classify(exception) == SqliteRemoteErrorClassification.Transient;

    internal static SqliteRemoteException From(Exception source, SqliteException mapped)
    {
        var statusCode = source is AhtolaException ahtola ? ahtola.RemoteStatusCode : null;
        var classification = Classify(source);
        return new SqliteRemoteException(mapped.Message, mapped.SqliteErrorCode, classification, statusCode);
    }
}
