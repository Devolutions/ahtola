using System.Globalization;

namespace Ahtola;

/// <summary>
/// Tunables for the persistent Hrana WebSocket transport. Every value is validated at
/// construction so a malformed connection string fails before a socket is opened.
/// </summary>
internal sealed class AhtolaHranaWebSocketOptions
{
    internal const int MinimumMaxMessageBytes = 8 * 1024;
    internal const int MaximumMaxMessageBytes = 512 * 1024 * 1024;
    internal const int MaximumConnectAttempts = 10;

    public static AhtolaHranaWebSocketOptions Default { get; } = new();

    /// <summary>
    /// WebSocket keep-alive ping interval handed to <c>ClientWebSocket</c>.
    /// <see cref="TimeSpan.Zero"/> disables keep-alives.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Grace period for a keep-alive pong. Honoured only on .NET 9+ where
    /// <c>ClientWebSocketOptions.KeepAliveTimeout</c> exists; on net8.0 the transport
    /// falls back to interval-only keep-alives and relies on the receive loop to observe
    /// the socket failure.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Silence budget for the application-level half-open watchdog.
    /// <see cref="TimeSpan.Zero"/> (the default) disables it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On net8.0 <c>ClientWebSocket</c> has no <c>KeepAliveTimeout</c>: its keep-alives are
    /// unsolicited frames that the peer never answers, so a half-open socket is invisible and
    /// every outstanding request hangs. Setting this value arms a watchdog that aborts the
    /// connection when nothing at all has arrived for this long while requests are
    /// outstanding. It sends no frames of its own, so it needs no protocol support.
    /// </para>
    /// <para>
    /// It is off by default on purpose. Without ping/pong a client cannot distinguish a dead
    /// peer from a server that is simply busy — a Hrana server sends nothing while a
    /// statement runs — so any non-zero budget also caps how long a single request may take.
    /// Set it above the longest statement the workload can issue, or leave it disabled and
    /// rely on <c>Command Timeout</c> (and, on .NET 9+, the runtime's real ping/pong
    /// timeout, which a busy server keeps answering).
    /// </para>
    /// </remarks>
    public TimeSpan HalfOpenTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>Hard cap on a single reassembled inbound message.</summary>
    public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Bounded connection-establishment attempts (never operation replay).</summary>
    public int ConnectAttempts { get; init; } = 3;

    /// <summary>Timeout for a single connect + hello handshake attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Base delay between connection-establishment attempts.</summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary><c>max_count</c> used by <c>fetch_cursor</c> paging.</summary>
    public int CursorPageSize { get; init; } = 128;

    /// <summary>Bounded outbound frame queue depth (backpressure, never unbounded buffering).</summary>
    public int SendQueueCapacity { get; init; } = 128;

    /// <summary>
    /// Ceiling on the cancelled-request tombstones one generation retains.
    /// </summary>
    /// <remarks>
    /// Tombstones let a late reply for an abandoned request be discarded instead of being
    /// mistaken for a multiplexing failure, so they can only be dropped when it is certain no
    /// reply can still arrive — which, over a live connection, it never is. The transport
    /// therefore retires the generation when this ceiling is crossed rather than evicting the
    /// oldest entries and risking a spurious "unknown request id" abort.
    /// </remarks>
    public int MaxCancelledRequestTombstones { get; init; } = 65536;

    /// <summary>Bound applied when disposal closes streams and the socket.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public AhtolaHranaWebSocketOptions Validate()
    {
        if (KeepAliveInterval < TimeSpan.Zero)
            throw new InvalidOperationException("Ws Keepalive Interval cannot be negative.");
        if (KeepAliveTimeout < TimeSpan.Zero)
            throw new InvalidOperationException("Ws Keepalive Timeout cannot be negative.");
        if (HalfOpenTimeout < TimeSpan.Zero)
            throw new InvalidOperationException("Ws Half Open Timeout cannot be negative.");
        if (MaxMessageBytes < MinimumMaxMessageBytes || MaxMessageBytes > MaximumMaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"Ws Max Message Bytes must be between {MinimumMaxMessageBytes.ToString(CultureInfo.InvariantCulture)} and "
                + $"{MaximumMaxMessageBytes.ToString(CultureInfo.InvariantCulture)}.");
        }
        if (ConnectAttempts < 1 || ConnectAttempts > MaximumConnectAttempts)
        {
            throw new InvalidOperationException(
                $"Ws Connect Attempts must be between 1 and {MaximumConnectAttempts.ToString(CultureInfo.InvariantCulture)}.");
        }
        if (ConnectTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Ws Connect Timeout must be positive.");
        if (CursorPageSize < 1)
            throw new InvalidOperationException("Ws Cursor Page Size must be positive.");
        if (SendQueueCapacity < 1)
            throw new InvalidOperationException("Ws Send Queue Capacity must be positive.");
        if (MaxCancelledRequestTombstones < 1)
            throw new InvalidOperationException("Ws Max Cancelled Request Tombstones must be positive.");
        if (CloseTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Ws Close Timeout must be positive.");

        return this;
    }
}
