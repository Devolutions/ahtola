using System.Collections;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite;

public class SqliteConnectionStringBuilder : DbConnectionStringBuilder
{
    private static readonly string[] CanonicalKeywords =
    [
        "Data Source",
        "Mode",
        "Cache",
        "Password",
        "Password Scheme",
        "Encryption Cipher",
        "Encryption Key",
        "Foreign Keys",
        "Recursive Triggers",
        "Default Timeout",
        "Pooling",
        "Vfs",
        "DateTimeKind",
        "DateTimeFormat",
        "BinaryGUID",
        "Version",
        "Local Provider",
        "Foreign Read Only",
        "Auth Token",
        "Replica Path",
        "Read Your Writes",
        "Sync Interval",
        "Tls",
        "Ws Keepalive Interval",
        "Ws Keepalive Timeout",
        "Ws Half Open Timeout",
        "Ws Max Message Bytes",
        "Ws Connect Attempts",
    ];

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
        ["Encryption Cipher"] = "Encryption Cipher",
        ["EncryptionCipher"] = "Encryption Cipher",
        ["Encryption Key"] = "Encryption Key",
        ["EncryptionKey"] = "Encryption Key",
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
        ["DateTimeKind"] = "DateTimeKind",
        ["Date Time Kind"] = "DateTimeKind",
        ["DateTimeFormat"] = "DateTimeFormat",
        ["Date Time Format"] = "DateTimeFormat",
        ["BinaryGUID"] = "BinaryGUID",
        ["BinaryGuid"] = "BinaryGUID",
        ["Binary GUID"] = "BinaryGUID",
        ["Version"] = "Version",
        ["Local Provider"] = "Local Provider",
        ["LocalProvider"] = "Local Provider",
        ["Foreign Read Only"] = "Foreign Read Only",
        ["ForeignReadOnly"] = "Foreign Read Only",
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
        // Hrana WebSocket (ws/wss) transport tunables; ignored by the HTTP pipeline.
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

    public SqliteConnectionStringBuilder()
    {
    }

    public SqliteConnectionStringBuilder(string? connectionString)
    {
        ConnectionString = connectionString ?? string.Empty;
    }

    public string DataSource
    {
        get => GetString("Data Source");
        set => SetString("Data Source", value);
    }

    public SqliteOpenMode Mode
    {
        get => GetEnum("Mode", SqliteOpenMode.ReadWriteCreate);
        set => this["Mode"] = value;
    }

    public SqliteCacheMode Cache
    {
        get => GetEnum("Cache", SqliteCacheMode.Default);
        set => this["Cache"] = value;
    }

    public string Password
    {
        get => GetString("Password");
        set => SetString("Password", value);
    }

    /// <summary>
    /// Passphrase key-derivation scheme id (for example <c>Ahtola.Password.v1</c>).
    /// Empty selects the catalog default. See <see cref="AhtolaPassphraseSchemes"/>.
    /// </summary>
    public string PasswordScheme
    {
        get => GetString("Password Scheme");
        set => SetString("Password Scheme", value);
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
        get => GetBool("Pooling", true);
        set => this["Pooling"] = value;
    }

    public string? Vfs
    {
        get => GetString("Vfs");
        set => SetString("Vfs", value);
    }

    public DateTimeKind DateTimeKind
    {
        get => GetEnum("DateTimeKind", System.DateTimeKind.Unspecified);
        set => this["DateTimeKind"] = value;
    }

    public string DateTimeFormat
    {
        get => GetString("DateTimeFormat");
        set => SetString("DateTimeFormat", value);
    }

    public bool BinaryGUID
    {
        get => GetBool("BinaryGUID", true);
        set => this["BinaryGUID"] = value;
    }

    public int Version
    {
        get => GetInt("Version", 3);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Version"] = value;
        }
    }

    public AhtolaLocalProvider LocalProvider
    {
        get => GetEnum("Local Provider", AhtolaLocalProvider.Native);
        set => this["Local Provider"] = value;
    }

    public bool IsLocalProviderConfigured => base.ContainsKey("Local Provider");

    internal AhtolaLocalProvider EffectiveLocalProvider => IsLocalProviderConfigured
        ? LocalProvider
        : AhtolaLocalProvider.Managed;

    /// <summary>
    /// Opens a database file owned by another engine without claiming ownership
    /// locks or requiring the shared-memory file. Requires <see cref="Mode"/>
    /// <see cref="SqliteOpenMode.ReadOnly"/>, the managed local provider, and
    /// <see cref="Pooling"/> disabled.
    /// </summary>
    public bool ForeignReadOnly
    {
        get => GetBool("Foreign Read Only");
        set => this["Foreign Read Only"] = value;
    }

    /// <summary>Authentication token used by a remote libsql or Turso endpoint.</summary>
    public string AuthToken
    {
        get => GetString("Auth Token");
        set => SetString("Auth Token", value);
    }

    /// <summary>Local path used for an embedded replica of a remote endpoint.</summary>
    public string ReplicaPath
    {
        get => GetString("Replica Path");
        set => SetString("Replica Path", value);
    }

    public bool ReadYourWrites
    {
        get => GetBool("Read Your Writes", true);
        set => this["Read Your Writes"] = value;
    }

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

    /// <summary>
    /// Hrana WebSocket keep-alive ping interval in seconds for <c>ws</c>/<c>wss</c> data
    /// sources; 0 disables keep-alives. Ignored by the HTTP pipeline transport.
    /// </summary>
    public int WsKeepaliveInterval
    {
        get => GetInt("Ws Keepalive Interval", 30);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Ws Keepalive Interval"] = value;
        }
    }

    /// <summary>
    /// Keep-alive pong grace period in seconds. Honoured on .NET 9 or newer; on net8.0
    /// only the interval is applied.
    /// </summary>
    public int WsKeepaliveTimeout
    {
        get => GetInt("Ws Keepalive Timeout", 20);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Ws Keepalive Timeout"] = value;
        }
    }

    /// <summary>
    /// Seconds of complete peer silence, while requests are outstanding, that abort a
    /// <c>ws</c>/<c>wss</c> connection as half-open. <c>0</c> (the default) disables the check.
    /// </summary>
    /// <remarks>
    /// This is the only half-open detection available on net8.0, where <c>ClientWebSocket</c>
    /// has no pong timeout. Because a Hrana server sends nothing while a statement runs, any
    /// non-zero value also caps how long a single request may take: set it above the longest
    /// statement the workload issues.
    /// </remarks>
    public int WsHalfOpenTimeout
    {
        get => GetInt("Ws Half Open Timeout", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Ws Half Open Timeout"] = value;
        }
    }

    /// <summary>Hard cap on a single reassembled Hrana WebSocket message, in bytes.</summary>
    public int WsMaxMessageBytes
    {
        get => GetInt("Ws Max Message Bytes", 16 * 1024 * 1024);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 8 * 1024);
            this["Ws Max Message Bytes"] = value;
        }
    }

    /// <summary>
    /// Bounded connection-establishment attempts for the Hrana WebSocket transport. It
    /// never replays in-flight operations.
    /// </summary>
    public int WsConnectAttempts
    {
        get => GetInt("Ws Connect Attempts", 3);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10);
            this["Ws Connect Attempts"] = value;
        }
    }

    public override ICollection Keys => new ReadOnlyCollection<string>(CanonicalKeywords);

    public override ICollection Values => new ReadOnlyCollection<object?>(CanonicalKeywords.Select(GetValueOrDefault).ToArray());

    [AllowNull]
    public override object this[string keyword]
    {
        get
        {
            var normalizedKeyword = NormalizeKeyword(keyword);
            return (base.TryGetValue(normalizedKeyword, out var value)
                ? ConvertFromStoredValue(normalizedKeyword, value)
                : GetValueOrDefault(normalizedKeyword))!;
        }
        set
        {
            var normalizedKeyword = NormalizeKeyword(keyword);
            if (value is null)
            {
                Remove(normalizedKeyword);
                return;
            }

            base[normalizedKeyword] = ConvertToStoredValue(normalizedKeyword, value);
        }
    }

    public override bool ContainsKey(string keyword) => KeywordMap.ContainsKey(keyword);

    public override bool Remove(string keyword)
    {
        if (!KeywordMap.TryGetValue(keyword, out var normalizedKeyword))
            return false;

        return base.Remove(normalizedKeyword);
    }

#pragma warning disable CS8765
    public override bool TryGetValue(string keyword, out object? value)
#pragma warning restore CS8765
    {
        if (!KeywordMap.TryGetValue(keyword, out var normalizedKeyword))
        {
            value = null;
            return false;
        }

        var result = base.TryGetValue(normalizedKeyword, out var storedValue)
            ? ConvertFromStoredValue(normalizedKeyword, storedValue)
            : GetValueOrDefault(normalizedKeyword);
        value = result;
        return true;
    }

    internal string GetAhtolaConnectionString()
    {
        var builder = new DbConnectionStringBuilder();
        if (!string.IsNullOrEmpty(DataSource))
            builder["Data Source"] = DataSource;
        if (base.ContainsKey("Mode"))
            builder["Mode"] = Mode.ToString();
        if (base.ContainsKey("Cache"))
            builder["Cache"] = Cache.ToString();
        if (base.ContainsKey("Foreign Keys"))
            builder["Foreign Keys"] = ForeignKeys!.Value;
        if (base.ContainsKey("Recursive Triggers"))
            builder["Recursive Triggers"] = RecursiveTriggers;
        if (base.ContainsKey("Default Timeout"))
            builder["Default Timeout"] = DefaultTimeout;
        if (base.ContainsKey("Pooling"))
            builder["Pooling"] = Pooling;
        if (base.ContainsKey("Auth Token"))
            builder["Auth Token"] = AuthToken;
        if (base.ContainsKey("Replica Path"))
            builder["Replica Path"] = ReplicaPath;
        if (base.ContainsKey("Read Your Writes"))
            builder["Read Your Writes"] = ReadYourWrites;
        if (base.ContainsKey("Sync Interval"))
            builder["Sync Interval"] = SyncInterval;
        if (base.ContainsKey("Tls"))
            builder["Tls"] = Tls!.Value;
        if (base.ContainsKey("Ws Keepalive Interval"))
            builder["Ws Keepalive Interval"] = WsKeepaliveInterval;
        if (base.ContainsKey("Ws Keepalive Timeout"))
            builder["Ws Keepalive Timeout"] = WsKeepaliveTimeout;
        if (base.ContainsKey("Ws Half Open Timeout"))
            builder["Ws Half Open Timeout"] = WsHalfOpenTimeout;
        if (base.ContainsKey("Ws Max Message Bytes"))
            builder["Ws Max Message Bytes"] = WsMaxMessageBytes;
        if (base.ContainsKey("Ws Connect Attempts"))
            builder["Ws Connect Attempts"] = WsConnectAttempts;
        if (base.ContainsKey("Encryption Cipher"))
            builder["Encryption Cipher"] = EncryptionCipher;
        if (base.ContainsKey("Encryption Key"))
            builder["Encryption Key"] = EncryptionKey;
        if (base.ContainsKey("Password"))
            builder["Password"] = Password;
        if (base.ContainsKey("Password Scheme"))
            builder["Password Scheme"] = PasswordScheme;
        if (base.ContainsKey("Local Provider"))
            builder["Local Provider"] = LocalProvider.ToString();
        else if (base.ContainsKey("Replica Path"))
            builder["Local Provider"] = AhtolaLocalProvider.Managed.ToString();

        return builder.ConnectionString;
    }

    internal AhtolaEncryptionOptions? CreateManagedEncryptionOptions()
    {
        var password = Password;
        var hasPassword = !string.IsNullOrEmpty(password);
        var passwordScheme = PasswordScheme;
        var cipher = GetString("Encryption Cipher");
        var keyConfigured = base.TryGetValue("Encryption Key", out var keyValue);
        var key = keyConfigured
            ? Convert.ToString(keyValue, CultureInfo.InvariantCulture)
            : null;
        var hasKey = !string.IsNullOrWhiteSpace(key);

        if (!hasPassword && !string.IsNullOrWhiteSpace(passwordScheme))
        {
            throw new InvalidOperationException(
                "Password Scheme requires Password=; it only selects passphrase key derivation.");
        }

        if (hasPassword && hasKey)
        {
            throw new InvalidOperationException(
                "Password and Encryption Key cannot be combined; use one passphrase or one hex key.");
        }

        if (hasPassword)
        {
            var scheme = AhtolaPassphraseSchemes.Resolve(passwordScheme);
            if (!string.IsNullOrWhiteSpace(cipher)
                && !CipherNameMatches(cipher, scheme.PageCipher))
            {
                throw new NotSupportedException(
                    $"Password Scheme '{scheme.Id}' derives {scheme.PageCipher}; "
                    + "Encryption Cipher must be omitted or match that page cipher.");
            }

            return scheme.DeriveEncryptionOptions(password);
        }

        if (string.IsNullOrWhiteSpace(cipher))
        {
            if (keyConfigured)
                throw new InvalidOperationException("Encryption Cipher is required when Encryption Key is specified.");

            return null;
        }

        if (!hasKey)
            throw new InvalidOperationException("Encryption Key is required when Encryption Cipher is specified.");

        return cipher.ToLowerInvariant() switch
        {
            "aes128gcm" or "aes-128-gcm" or "aes_128_gcm"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes128Gcm, key!),
            "aes256gcm" or "aes-256-gcm" or "aes_256_gcm"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes256Gcm, key!),
            "aegis256" or "aegis-256" or "aegis_256"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256, key!),
            "aegis256x2" or "aegis-256x2" or "aegis_256x2"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256X2, key!),
            "aegis256x4" or "aegis-256x4" or "aegis_256x4"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256X4, key!),
            "aegis128l" or "aegis-128l" or "aegis_128l"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128L, key!),
            "aegis128x2" or "aegis-128x2" or "aegis_128x2"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128X2, key!),
            "aegis128x4" or "aegis-128x4" or "aegis_128x4"
                => AhtolaEncryptionOptions.FromHex(Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128X4, key!),
            _ => throw new NotSupportedException(
                "Local Provider=Managed supports only Ahtola encrypted format version 0 cipher IDs 1 through 8 "
                + "(AES128GCM, AES256GCM, AEGIS256, AEGIS256X2, AEGIS256X4, AEGIS128L, AEGIS128X2, AEGIS128X4); "
                + "cipher fallback is not permitted."),
        };
    }

    internal bool HasEncryptionOptions
        => base.ContainsKey("Encryption Cipher")
           || base.ContainsKey("Encryption Key")
           || !string.IsNullOrEmpty(Password);

    private static bool CipherNameMatches(string cipherName, Ahtola.Core.Storage.AhtolaEncryptionCipher cipher)
        => cipherName.ToLowerInvariant() switch
        {
            "aes128gcm" or "aes-128-gcm" or "aes_128_gcm"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes128Gcm,
            "aes256gcm" or "aes-256-gcm" or "aes_256_gcm"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            "aegis256" or "aegis-256" or "aegis_256"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256,
            "aegis256x2" or "aegis-256x2" or "aegis_256x2"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256X2,
            "aegis256x4" or "aegis-256x4" or "aegis_256x4"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256X4,
            "aegis128l" or "aegis-128l" or "aegis_128l"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128L,
            "aegis128x2" or "aegis-128x2" or "aegis_128x2"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128X2,
            "aegis128x4" or "aegis-128x4" or "aegis_128x4"
                => cipher == Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128X4,
            _ => false,
        };

    private static string NormalizeKeyword(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (KeywordMap.TryGetValue(keyword, out var normalizedKeyword))
            return normalizedKeyword;

        throw new ArgumentException(Properties.Resources.KeywordNotSupported(keyword));
    }

    private string GetString(string keyword)
    {
        return base.TryGetValue(keyword, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
    }

    private void SetString(string keyword, string? value)
    {
        if (value is null)
            Remove(keyword);
        else
            this[keyword] = value;
    }

    private bool GetBool(string keyword, bool defaultValue = false)
    {
        return base.TryGetValue(keyword, out var value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : defaultValue;
    }

    private bool? GetNullableBool(string keyword)
    {
        return base.TryGetValue(keyword, out var value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : null;
    }

    private int GetInt(string keyword, int defaultValue)
    {
        return base.TryGetValue(keyword, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : defaultValue;
    }

    private TEnum GetEnum<TEnum>(string keyword, TEnum defaultValue)
        where TEnum : struct
    {
        if (!base.TryGetValue(keyword, out var value))
            return defaultValue;

        if (value is TEnum typedValue)
        {
            if (!Enum.IsDefined(typeof(TEnum), typedValue))
                throw new ArgumentOutOfRangeException(nameof(value), value, Properties.Resources.InvalidEnumValue(typeof(TEnum), typedValue));

            return typedValue;
        }

        if (value is string stringValue && Enum.TryParse<TEnum>(stringValue, ignoreCase: true, out var parsedValue))
            return parsedValue;

        return (TEnum)Enum.ToObject(typeof(TEnum), Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private void SetNullable<T>(string keyword, T? value)
        where T : struct
    {
        if (value.HasValue)
            this[keyword] = value.Value;
        else
            Remove(keyword);
    }

    private static object? ConvertToStoredValue(string keyword, object value)
    {
        return keyword switch
        {
            "Mode" => ConvertOpenMode(value),
            "Cache" => ConvertCacheMode(value),
            "Foreign Keys" => ConvertToNullableBoolean(value),
            "Recursive Triggers" or "Pooling" or "BinaryGUID" or "Foreign Read Only" or "Read Your Writes" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "Tls" => ConvertToNullableBoolean(value),
            "Default Timeout" or "Version" or "Sync Interval" or "Ws Keepalive Interval" or "Ws Keepalive Timeout"
                or "Ws Half Open Timeout" or "Ws Max Message Bytes" or "Ws Connect Attempts"
                => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            "DateTimeKind" => ConvertDateTimeKind(value),
            "Local Provider" => ConvertLocalProvider(value),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static object? ConvertFromStoredValue(string keyword, object value)
    {
        return keyword switch
        {
            "Mode" => ConvertOpenMode(value),
            "Cache" => ConvertCacheMode(value),
            "Foreign Keys" => ConvertToNullableBoolean(value)!,
            "Tls" => ConvertToNullableBoolean(value)!,
            "Read Your Writes" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "DateTimeKind" => ConvertDateTimeKind(value),
            "Local Provider" => ConvertLocalProvider(value),
            _ => value,
        };
    }

    private object? GetValueOrDefault(string keyword)
    {
        return keyword switch
        {
            "Data Source" => string.Empty,
            "Mode" => SqliteOpenMode.ReadWriteCreate,
            "Cache" => SqliteCacheMode.Default,
            "Password" => string.Empty,
            "Password Scheme" => string.Empty,
            "Encryption Cipher" => string.Empty,
            "Encryption Key" => string.Empty,
            "Foreign Keys" => null!,
            "Recursive Triggers" => false,
            "Default Timeout" => 30,
            "Pooling" => true,
            "Vfs" => null!,
            "DateTimeKind" => System.DateTimeKind.Unspecified,
            "DateTimeFormat" => string.Empty,
            "BinaryGUID" => true,
            "Version" => 3,
            "Local Provider" => AhtolaLocalProvider.Native,
            "Foreign Read Only" => false,
            "Auth Token" => string.Empty,
            "Replica Path" => string.Empty,
            "Read Your Writes" => true,
            "Sync Interval" => 0,
            "Tls" => null!,
            "Ws Keepalive Interval" => 30,
            "Ws Keepalive Timeout" => 20,
            "Ws Half Open Timeout" => 0,
            "Ws Max Message Bytes" => 16 * 1024 * 1024,
            "Ws Connect Attempts" => 3,
            _ => throw new ArgumentException(Properties.Resources.KeywordNotSupported(keyword)),
        };
    }

    private static TEnum ConvertEnum<TEnum>(object value)
        where TEnum : struct
    {
        if (value is TEnum typedValue)
            return typedValue;

        if (value is string stringValue)
            return Enum.Parse<TEnum>(stringValue, ignoreCase: true);

        if (value.GetType().IsEnum && value is not TEnum)
            throw new ArgumentException(Properties.Resources.ConvertFailed(value.GetType(), typeof(TEnum)));

        var enumValue = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(typeof(TEnum), enumValue))
            throw new ArgumentOutOfRangeException(nameof(value), value, Properties.Resources.InvalidEnumValue(typeof(TEnum), enumValue));

        return enumValue;
    }

    private static bool? ConvertToNullableBoolean(object value)
        => value is null or string { Length: 0 }
            ? null
            : Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static SqliteOpenMode ConvertOpenMode(object value)
    {
        var mode = ConvertEnum<SqliteOpenMode>(value);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(value), value, Properties.Resources.InvalidEnumValue(typeof(SqliteOpenMode), mode));

        return mode;
    }

    private static SqliteCacheMode ConvertCacheMode(object value)
    {
        var mode = ConvertEnum<SqliteCacheMode>(value);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(value), value, Properties.Resources.InvalidEnumValue(typeof(SqliteCacheMode), mode));

        return mode;
    }

    private static DateTimeKind ConvertDateTimeKind(object value)
    {
        var kind = ConvertEnum<DateTimeKind>(value);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(value), value, Properties.Resources.InvalidEnumValue(typeof(DateTimeKind), kind));

        return kind;
    }

    private static AhtolaLocalProvider ConvertLocalProvider(object value)
    {
        var provider = ConvertEnum<AhtolaLocalProvider>(value);
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                Properties.Resources.InvalidEnumValue(typeof(AhtolaLocalProvider), provider));

        return provider;
    }
}
