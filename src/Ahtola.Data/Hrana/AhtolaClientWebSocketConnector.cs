using System.Net.WebSockets;

namespace Ahtola;

/// <summary>
/// Opens the underlying WebSocket for the Hrana transport. Abstracted so tests can
/// substitute an in-memory <see cref="WebSocket"/> without reflection or a mocking
/// framework; production always uses <see cref="AhtolaClientWebSocketConnector"/>.
/// </summary>
internal interface IAhtolaWebSocketConnector
{
    Task<WebSocket> ConnectAsync(
        Uri endpoint,
        IReadOnlyList<string> subProtocols,
        AhtolaHranaWebSocketOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production connector built on <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// Certificate validation is left to the platform: no
/// <c>RemoteCertificateValidationCallback</c> override and no certificate bypass, matching
/// <see cref="AhtolaRemoteTransportSecurity.CreateRedirectSafeHttpClient"/>.
/// </para>
/// <para>
/// <c>ClientWebSocket</c> never follows HTTP redirects during the upgrade; any non-101
/// response surfaces as a <see cref="WebSocketException"/>, which is the fail-closed
/// behaviour this transport wants (the HTTP pipeline follows only 307/308, and an
/// Upgrade handshake has no equivalent method/body preservation guarantee).
/// </para>
/// </remarks>
internal sealed class AhtolaClientWebSocketConnector : IAhtolaWebSocketConnector
{
    public static AhtolaClientWebSocketConnector Instance { get; } = new();

    public async Task<WebSocket> ConnectAsync(
        Uri endpoint,
        IReadOnlyList<string> subProtocols,
        AhtolaHranaWebSocketOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(subProtocols);
        ArgumentNullException.ThrowIfNull(options);

        var socket = CreateSocket(subProtocols, options);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the configured <see cref="ClientWebSocket"/>. Exposed so tests can assert the
    /// keep-alive and certificate-validation policy without opening a real socket.
    /// </summary>
    internal static ClientWebSocket CreateSocket(IReadOnlyList<string> subProtocols, AhtolaHranaWebSocketOptions options)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var subProtocol in subProtocols)
                socket.Options.AddSubProtocol(subProtocol);

            socket.Options.KeepAliveInterval = options.KeepAliveInterval;
#if NET9_0_OR_GREATER
            // .NET 9 added a real ping/pong timeout: with both set, the runtime sends PING
            // frames and aborts the socket when a PONG does not arrive in time. On net8.0
            // only the interval exists (unsolicited keep-alives), so a dead peer is
            // detected by the receive loop instead.
            if (options.KeepAliveInterval > TimeSpan.Zero && options.KeepAliveTimeout > TimeSpan.Zero)
                socket.Options.KeepAliveTimeout = options.KeepAliveTimeout;
#endif

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
