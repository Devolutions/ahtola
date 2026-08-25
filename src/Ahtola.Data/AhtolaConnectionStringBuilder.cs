using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Ahtola;

public sealed class AhtolaConnectionStringBuilder : DbConnectionStringBuilder
{
    private static readonly Dictionary<string, string> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Data Source"] = "Data Source",
        ["DataSource"] = "Data Source",
        ["Filename"] = "Data Source",
        ["Mode"] = "Mode",
        ["Cache"] = "Cache",
        ["Password"] = "Password",
        ["Password Scheme"] = "Password Scheme",
        ["PasswordScheme"] = "Password Scheme",
        ["Foreign Keys"] = "Foreign Keys",
        ["ForeignKeys"] = "Foreign Keys",
        ["Recursive Triggers"] = "Recursive Triggers",
        ["RecursiveTriggers"] = "Recursive Triggers",
        ["Default Timeout"] = "Default Timeout",
        ["DefaultTimeout"] = "Default Timeout",
        ["Command Timeout"] = "Default Timeout",
        ["CommandTimeout"] = "Default Timeout",
        ["Pooling"] = "Pooling",
        ["Vfs"] = "Vfs",
        ["Encryption Cipher"] = "Encryption Cipher",
        ["EncryptionCipher"] = "Encryption Cipher",
        ["Encryption Key"] = "Encryption Key",
        ["EncryptionKey"] = "Encryption Key",
        ["Auth Token"] = "Auth Token",
        ["AuthToken"] = "Auth Token",
        ["Authentication Token"] = "Auth Token",
        ["AuthenticationToken"] = "Auth Token",
        ["Replica Path"] = "Replica Path",
        ["ReplicaPath"] = "Replica Path",
        ["Read Your Writes"] = "Read Your Writes",
        ["ReadYourWrites"] = "Read Your Writes",
        ["Sync Interval"] = "Sync Interval",
        ["SyncInterval"] = "Sync Interval",
        ["Tls"] = "Tls",
        ["TLS"] = "Tls",
        ["Local Provider"] = "Local Provider",
        ["LocalProvider"] = "Local Provider",
        ["Foreign Read Only"] = "Foreign Read Only",
        ["ForeignReadOnly"] = "Foreign Read Only",
        // Hrana WebSocket (ws/wss) transport tunables.
        ["Ws Keepalive Interval"] = "Ws Keepalive Interval",
        ["WsKeepaliveInterval"] = "Ws Keepalive Interval",
        ["WebSocket Keepalive Interval"] = "Ws Keepalive Interval",
        ["WebSocketKeepAliveInterval"] = "Ws Keepalive Interval",
        ["Ws Keepalive Timeout"] = "Ws Keepalive Timeout",
        ["WsKeepaliveTimeout"] = "Ws Keepalive Timeout",
        ["WebSocket Keepalive Timeout"] = "Ws Keepalive Timeout",
        ["WebSocketKeepAliveTimeout"] = "Ws Keepalive Timeout",
        ["Ws Half Open Timeout"] = "Ws Half Open Timeout",
        ["WsHalfOpenTimeout"] = "Ws Half Open Timeout",
        ["WebSocket Half Open Timeout"] = "Ws Half Open Timeout",
        ["WebSocketHalfOpenTimeout"] = "Ws Half Open Timeout",
        ["Ws Max Message Bytes"] = "Ws Max Message Bytes",
        ["WsMaxMessageBytes"] = "Ws Max Message Bytes",
        ["WebSocket Max Message Bytes"] = "Ws Max Message Bytes",
        ["WebSocketMaxMessageBytes"] = "Ws Max Message Bytes",
        ["Ws Connect Attempts"] = "Ws Connect Attempts",
        ["WsConnectAttempts"] = "Ws Connect Attempts",
        ["WebSocket Connect Attempts"] = "Ws Connect Attempts",
        ["WebSocketConnectAttempts"] = "Ws Connect Attempts",
    };

    public AhtolaConnectionStringBuilder()
    {
    }

    public AhtolaConnectionStringBuilder(string? connectionString)
    {
        ConnectionString = connectionString ?? string.Empty;
    }

    public string DataSource
    {
        get => GetString("Data Source");
        set => SetString("Data Source", value);
    }

    public string Mode
    {
        get => GetString("Mode");
        set => SetString("Mode", value);
    }

    public string Cache
    {
        get => GetString("Cache");
        set => SetString("Cache", value);
    }

    public string Password
    {
        get => GetString("Password");
        set => SetString("Password", value);
    }

    /// <summary>
    /// Passphrase key-derivation scheme id (for example <c>Ahtola.Password.v1</c>).
    /// Empty selects the catalog default.
    /// </summary>
    public string PasswordScheme
    {
        get => GetString("Password Scheme");
        set => SetString("Password Scheme", value);
    }

    public bool? ForeignKeys
    {
        get => GetNullableBool("Foreign Keys");
        set => SetNullable("Foreign Keys", value);
    }

    public bool RecursiveTriggers
    {
        get => GetBool("Recursive Triggers");
        set => this["Recursive Triggers"] = value;
    }

    public int DefaultTimeout
    {
        get => GetInt("Default Timeout", 30);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Default Timeout"] = value;
        }
    }

    public bool Pooling
    {
        get => GetBool("Pooling");
        set => this["Pooling"] = value;
    }

    public string Vfs
    {
        get => GetString("Vfs");
        set => SetString("Vfs", value);
    }

    public string EncryptionCipher
    {
        get => GetString("Encryption Cipher");
        set => SetString("Encryption Cipher", value);
    }

    public string EncryptionKey
    {
        get => GetString("Encryption Key");
        set => SetString("Encryption Key", value);
    }

    public string AuthToken
    {
        get => GetString("Auth Token");
        set => SetString("Auth Token", value);
    }

    public string ReplicaPath
    {
        get => GetString("Replica Path");
        set => SetString("Replica Path", value);
    }

    public bool ReadYourWrites
    {
        get => GetBool("Read Your Writes", defaultValue: true);
        set => this["Read Your Writes"] = value;
    }

    /// <summary>
    /// Gets or sets the managed embedded-replica automatic synchronization interval.
    /// </summary>
    /// <remarks>
    /// Positive values are measured in seconds and are supported only by managed
    /// embedded replica connections.
    /// </remarks>
    public int SyncInterval
    {
        get => GetInt("Sync Interval", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Sync Interval"] = value;
        }
    }

    public bool? Tls
    {
        get => GetNullableBool("Tls");
        set => SetNullable("Tls", value);
    }

    public AhtolaLocalProvider LocalProvider
    {
        get => GetEnum("Local Provider", AhtolaLocalProvider.Native);
        set => this["Local Provider"] = value;
    }

    /// <summary>
    /// Opens a database file owned by another engine without claiming ownership
    /// locks or requiring the shared-memory file. Requires Local Provider=Managed,
    /// Mode=ReadOnly, and Pooling=False.
    /// </summary>
    public bool ForeignReadOnly
    {
        get => GetBool("Foreign Read Only");
        set => this["Foreign Read Only"] = value;
    }

    internal bool IsLocalProviderConfigured => base.ContainsKey("Local Provider");

    /// <summary>
    /// WebSocket keep-alive ping interval in seconds for <c>ws</c>/<c>wss</c> data sources.
    /// 0 disables keep-alives. Ignored by the HTTP pipeline transport.
    /// </summary>
    public int WsKeepaliveInterval
    {
        get => GetInt("Ws Keepalive Interval", (int)AhtolaHranaWebSocketOptions.Default.KeepAliveInterval.TotalSeconds);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Ws Keepalive Interval"] = value;
        }
    }

    /// <summary>
    /// Keep-alive pong grace period in seconds for <c>ws</c>/<c>wss</c> data sources.
    /// Honoured on .NET 9 or newer; on net8.0 only the interval is applied.
    /// </summary>
    public int WsKeepaliveTimeout
    {
        get => GetInt("Ws Keepalive Timeout", (int)AhtolaHranaWebSocketOptions.Default.KeepAliveTimeout.TotalSeconds);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Ws Keepalive Timeout"] = value;
        }
    }

    /// <summary>
    /// Seconds of complete peer silence, while requests are outstanding, that abort a
    /// <c>ws</c>/<c>wss</c> connection as half-open. <c>0</c> (the default) disables the
    /// check.
    /// </summary>
    /// <remarks>
    /// This is the only half-open detection available on net8.0, where
    /// <c>ClientWebSocket</c> has no pong timeout. Because a Hrana server sends nothing while
    /// a statement runs, any non-zero value also caps how long a single request may take:
    /// set it above the longest statement the workload issues.
    /// </remarks>
    public int WsHalfOpenTimeout
    {
        get => GetInt("Ws Half Open Timeout", (int)AhtolaHranaWebSocketOptions.Default.HalfOpenTimeout.TotalSeconds);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Ws Half Open Timeout"] = value;
        }
    }

    /// <summary>Hard cap on a single reassembled Hrana WebSocket message, in bytes.</summary>
    public int WsMaxMessageBytes
    {
        get => GetInt("Ws Max Message Bytes", AhtolaHranaWebSocketOptions.Default.MaxMessageBytes);
        set
        {
            if (value is < AhtolaHranaWebSocketOptions.MinimumMaxMessageBytes
                or > AhtolaHranaWebSocketOptions.MaximumMaxMessageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Ws Max Message Bytes must be between {AhtolaHranaWebSocketOptions.MinimumMaxMessageBytes} and {AhtolaHranaWebSocketOptions.MaximumMaxMessageBytes}.");
            }

            this["Ws Max Message Bytes"] = value;
        }
    }

    /// <summary>
    /// Bounded connection-establishment attempts for the Hrana WebSocket transport. This
    /// never replays in-flight operations; it only bounds how often a brand-new connection
    /// is attempted before an operation fails.
    /// </summary>
    public int WsConnectAttempts
    {
        get => GetInt("Ws Connect Attempts", AhtolaHranaWebSocketOptions.Default.ConnectAttempts);
        set
        {
            if (value is < 1 or > AhtolaHranaWebSocketOptions.MaximumConnectAttempts)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Ws Connect Attempts must be between 1 and {AhtolaHranaWebSocketOptions.MaximumConnectAttempts}.");
            }

            this["Ws Connect Attempts"] = value;
        }
    }

    [AllowNull]
    public override object this[string keyword]
    {
        get => base[NormalizeKeyword(keyword)];
        set
        {
            var normalizedKeyword = NormalizeKeyword(keyword);
            if (value is null)
            {
                Remove(normalizedKeyword);
                return;
            }

            base[normalizedKeyword] = value;
        }
    }

    public override bool ContainsKey(string keyword) => base.ContainsKey(NormalizeKeyword(keyword));

    public override bool Remove(string keyword) => base.Remove(NormalizeKeyword(keyword));

    public override bool TryGetValue(string keyword, out object value)
    {
        var found = base.TryGetValue(NormalizeKeyword(keyword), out var result);
        value = result!;
        return found;
    }

    internal static ReadOnlyCollection<string> ValidKeywords { get; } =
        new(KeywordMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    internal string? GetOption(string keyword)
    {
        return TryGetValue(keyword, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
    }

    internal AhtolaEncryptionCipher? GetEncryptionCipher()
    {
        var cipher = GetOption("Encryption Cipher");
        if (string.IsNullOrWhiteSpace(cipher))
            return null;

        return cipher.ToLowerInvariant() switch
        {
            "aes128gcm" or "aes-128-gcm" or "aes_128_gcm" => AhtolaEncryptionCipher.Aes128Gcm,
            "aes256gcm" or "aes-256-gcm" or "aes_256_gcm" => AhtolaEncryptionCipher.Aes256Gcm,
            "aegis256" or "aegis-256" or "aegis_256" => AhtolaEncryptionCipher.Aegis256,
            "aegis256x2" or "aegis-256x2" or "aegis_256x2" => AhtolaEncryptionCipher.Aegis256x2,
            "aegis256x4" or "aegis-256x4" or "aegis_256x4" => AhtolaEncryptionCipher.Aegis256x4,
            "aegis128l" or "aegis-128l" or "aegis_128l" => AhtolaEncryptionCipher.Aegis128l,
            "aegis128x2" or "aegis-128x2" or "aegis_128x2" => AhtolaEncryptionCipher.Aegis128x2,
            "aegis128x4" or "aegis-128x4" or "aegis_128x4" => AhtolaEncryptionCipher.Aegis128x4,
            _ => throw new InvalidOperationException($"Unknown encryption cipher: {cipher}")
        };
    }

    private static string NormalizeKeyword(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (KeywordMap.TryGetValue(keyword, out var normalizedKeyword))
            return normalizedKeyword;

        throw new ArgumentException($"Unsupported keyword: {keyword}", nameof(keyword));
    }

    private string GetString(string keyword) => GetOption(keyword) ?? string.Empty;

    private void SetString(string keyword, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this[keyword] = value;
    }

    private bool GetBool(string keyword, bool defaultValue = false)
    {
        return TryGetValue(keyword, out var value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : defaultValue;
    }

    private bool? GetNullableBool(string keyword)
    {
        return TryGetValue(keyword, out var value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : null;
    }

    private int GetInt(string keyword, int defaultValue)
    {
        return TryGetValue(keyword, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : defaultValue;
    }

    private TEnum GetEnum<TEnum>(string keyword, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!TryGetValue(keyword, out var value))
            return defaultValue;

        if (value is TEnum typedValue && Enum.IsDefined(typedValue))
            return typedValue;

        if (value is string stringValue
            && Enum.TryParse<TEnum>(stringValue, ignoreCase: true, out var parsedValue)
            && Enum.IsDefined(parsedValue))
        {
            return parsedValue;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, $"Invalid {keyword} value.");
    }

    private void SetNullable<T>(string keyword, T? value)
        where T : struct
    {
        if (value.HasValue)
            this[keyword] = value.Value;
        else
            Remove(keyword);
    }
}
