using System.Globalization;
using Ahtola.Core.Storage;
using ManagedEncryptionOptions = Ahtola.Core.Storage.AhtolaEncryptionOptions;

namespace Ahtola;

public class AhtolaConnectionOptions
{
    private readonly AhtolaConnectionStringBuilder _builder;

    private AhtolaConnectionOptions(AhtolaConnectionStringBuilder builder)
    {
        _builder = builder;
    }

    public string GetConnectionString() => _builder.ConnectionString;

    public string? this[string keyword]
    {
        get => _builder.GetOption(keyword);
        set => _builder[keyword] = value ?? string.Empty;
    }

    public int DefaultTimeout => _builder.DefaultTimeout;

    public string DataSource => _builder.DataSource;

    public string Mode => _builder.Mode;

    public string Cache => _builder.Cache;

    public string AuthToken => _builder.AuthToken;

    public string ReplicaPath => _builder.ReplicaPath;

    public bool ReadYourWrites => _builder.ReadYourWrites;

    public bool Pooling => _builder.Pooling;

    public bool? ForeignKeys => _builder.ForeignKeys;

    public bool RecursiveTriggers => _builder.RecursiveTriggers;

    public bool ForeignReadOnly => _builder.ForeignReadOnly;

    public int SyncInterval => _builder.SyncInterval;

    public bool? Tls => _builder.Tls;

    public AhtolaLocalProvider LocalProvider => _builder.IsLocalProviderConfigured
        ? _builder.LocalProvider
        : IsRemote
            ? AhtolaLocalProvider.Native
            : AhtolaLocalProvider.Managed;

    public bool IsRemote => AhtolaConnectionModeClassifier.Classify(DataSource, ReplicaPath)
        != AhtolaConnectionEndpointMode.Local;

    public bool IsReplica => AhtolaConnectionModeClassifier.Classify(DataSource, ReplicaPath)
        == AhtolaConnectionEndpointMode.EmbeddedReplica;

    internal bool HasRecursiveTriggers => _builder.GetOption("Recursive Triggers") is not null;

    public AhtolaEncryptionCipher? GetEncryptionCipher() => _builder.GetEncryptionCipher();

    internal AhtolaRemoteEncryptionOptions? GetRemoteEncryptionOptions()
    {
        var cipher = GetEncryptionCipher();
        var key = _builder.GetOption("Encryption Key");
        if (cipher is null && string.IsNullOrWhiteSpace(key))
            return null;
        if (cipher is null)
            throw new InvalidOperationException("Encryption Cipher is required when Encryption Key is specified.");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Encryption Key is required when Encryption Cipher is specified.");

        return new AhtolaRemoteEncryptionOptions(
            key,
            cipher.Value switch
            {
                AhtolaEncryptionCipher.Aes128Gcm => AhtolaRemoteEncryptionCipher.Aes128Gcm,
                AhtolaEncryptionCipher.Aes256Gcm => AhtolaRemoteEncryptionCipher.Aes256Gcm,
                AhtolaEncryptionCipher.Aegis256 => AhtolaRemoteEncryptionCipher.Aegis256,
                AhtolaEncryptionCipher.Aegis256x2 => AhtolaRemoteEncryptionCipher.Aegis256X2,
                AhtolaEncryptionCipher.Aegis256x4 => AhtolaRemoteEncryptionCipher.Aegis256X4,
                AhtolaEncryptionCipher.Aegis128l => AhtolaRemoteEncryptionCipher.Aegis128L,
                AhtolaEncryptionCipher.Aegis128x2 => AhtolaRemoteEncryptionCipher.Aegis128X2,
                AhtolaEncryptionCipher.Aegis128x4 => AhtolaRemoteEncryptionCipher.Aegis128X4,
                _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher, "Unknown remote encryption cipher."),
            });
    }

    internal ManagedLocalOpenOptions GetManagedLocalOpenOptions()
    {
        var mode = ParseManagedOpenMode(Mode);
        var dataSource = string.IsNullOrEmpty(DataSource) ? ":memory:" : DataSource;
        var cache = Cache;
        var sharedMemoryName = default(string);
        if (!string.IsNullOrWhiteSpace(cache)
            && !cache.Equals("Default", StringComparison.OrdinalIgnoreCase)
            && !cache.Equals("Private", StringComparison.OrdinalIgnoreCase))
        {
            if (cache.Equals("Shared", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == ManagedLocalOpenMode.Memory && !string.IsNullOrWhiteSpace(DataSource))
                {
                    // Microsoft.Data.Sqlite routes Mode=Memory + Cache=Shared through the
                    // shared-cache URI form, so any named Data Source (including a literal
                    // ":memory:") becomes one in-memory database shared per process.
                    sharedMemoryName = DataSource;
                }
                else if (!dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
                {
                    // File-backed Cache=Shared opens as an ordinary private file
                    // connection. The managed engine cannot emulate SQLite shared-cache
                    // semantics (cross-connection dirty reads, table locks) for file
                    // databases, so it deliberately provides stronger (private)
                    // isolation instead of rejecting the keyword outright.
                }

                // Every remaining shape resolves to an anonymous :memory: database, which
                // SQLite always keeps connection-private even under shared-cache mode.
            }
            else
            {
                throw new ArgumentException($"Invalid Cache value for Local Provider=Managed: {cache}.", nameof(Cache));
            }
        }

        if (mode is ManagedLocalOpenMode.ReadOnly or ManagedLocalOpenMode.ReadWrite
            && dataSource == ":memory:")
        {
            throw new InvalidOperationException($"Mode={Mode} requires an existing database file when Local Provider=Managed.");
        }

        if (mode is ManagedLocalOpenMode.ReadOnly or ManagedLocalOpenMode.ReadWrite && !File.Exists(dataSource))
            throw new InvalidOperationException($"Mode={Mode} requires an existing database file when Local Provider=Managed.");

        if (!string.IsNullOrWhiteSpace(_builder.GetOption("Vfs")))
        {
            throw new NotSupportedException(
                "Vfs is not supported when Local Provider=Managed because the managed engine does not use native SQLite VFS implementations.");
        }

        var timeout = DefaultTimeout;
        if (timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout), timeout, "Default Timeout cannot be negative.");

        var managedDataSource = mode == ManagedLocalOpenMode.Memory ? ":memory:" : dataSource;
        var encryption = CreateManagedEncryptionOptions(mode, managedDataSource);
        if (ForeignReadOnly
            && (mode != ManagedLocalOpenMode.ReadOnly || Pooling || encryption is not null || sharedMemoryName is not null))
        {
            encryption?.Dispose();
            throw new NotSupportedException(
                "Foreign Read Only requires Local Provider=Managed, Mode=ReadOnly, Pooling=False, a file-backed Data Source, and no shared cache or encryption options.");
        }

        return new ManagedLocalOpenOptions(
            managedDataSource,
            mode == ManagedLocalOpenMode.ReadOnly,
            encryption,
            sharedMemoryName,
            ForeignReadOnly);
    }

    public Uri GetRemoteUri()
    {
        if (!Uri.TryCreate(DataSource, UriKind.Absolute, out var uri)
            || !AhtolaConnectionModeClassifier.IsRemoteScheme(uri.Scheme))
            throw new InvalidOperationException($"Data Source is not a remote Ahtola URL: {DataSource}");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Remote Ahtola URLs must not include query strings or fragments.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Remote Ahtola URLs must not include embedded user information; use Auth Token instead.");
        if (string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("Remote Ahtola URLs must include a host.");

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            // turso is the scheme the Turso dashboard hands out; it addresses the same endpoint as libsql.
            "libsql" or "turso" => Tls == false ? "http" : "https",
            "http" => ValidateTls(uri.Scheme, expectedTls: false),
            "https" => ValidateTls(uri.Scheme, expectedTls: true),
            "ws" => ValidateTls(uri.Scheme, expectedTls: false, normalizedScheme: "http"),
            "wss" => ValidateTls(uri.Scheme, expectedTls: true, normalizedScheme: "https"),
            _ => throw new InvalidOperationException($"Unsupported remote Ahtola URL scheme: {uri.Scheme}")
        };

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return builder.Uri;
    }

    /// <summary>
    /// True when the data source addresses a Hrana WebSocket endpoint (<c>ws</c>/<c>wss</c>).
    /// </summary>
    /// <remarks>
    /// <c>http</c>, <c>https</c>, <c>libsql</c> and <c>turso</c> stay on the HTTP pipeline;
    /// only the explicit WebSocket schemes select the persistent transport.
    /// </remarks>
    internal bool IsWebSocketRemote
    {
        get
        {
            if (IsReplica || !Uri.TryCreate(DataSource, UriKind.Absolute, out var uri))
                return false;

            return AhtolaRemoteClient.IsWebSocketScheme(uri.Scheme);
        }
    }

    /// <summary>
    /// Builds the <c>ws</c>/<c>wss</c> endpoint used by the Hrana WebSocket transport.
    /// </summary>
    /// <remarks>
    /// The upgrade happens on the URL's own path (the sqld server registers the WebSocket
    /// route on <c>"/"</c>); the <c>/v2</c> and <c>/v3</c> suffixes belong to the HTTP
    /// pipeline only and are never appended here.
    /// </remarks>
    internal Uri GetRemoteWebSocketUri()
    {
        if (!Uri.TryCreate(DataSource, UriKind.Absolute, out var uri)
            || !AhtolaRemoteClient.IsWebSocketScheme(uri.Scheme))
        {
            throw new InvalidOperationException($"Data Source is not a remote Ahtola WebSocket URL: {DataSource}");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Remote Ahtola URLs must not include query strings or fragments.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Remote Ahtola URLs must not include embedded user information; use Auth Token instead.");
        if (string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("Remote Ahtola URLs must include a host.");

        var scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            ? ValidateTls(uri.Scheme, expectedTls: true, normalizedScheme: "wss")
            : ValidateTls(uri.Scheme, expectedTls: false, normalizedScheme: "ws");

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return builder.Uri;
    }

    /// <summary>Reads the validated Hrana WebSocket tunables from the connection string.</summary>
    internal AhtolaHranaWebSocketOptions GetWebSocketOptions()
    {
        var defaults = AhtolaHranaWebSocketOptions.Default;
        return new AhtolaHranaWebSocketOptions
        {
            KeepAliveInterval = ReadSeconds("Ws Keepalive Interval", defaults.KeepAliveInterval),
            KeepAliveTimeout = ReadSeconds("Ws Keepalive Timeout", defaults.KeepAliveTimeout),
            HalfOpenTimeout = ReadSeconds("Ws Half Open Timeout", defaults.HalfOpenTimeout),
            MaxMessageBytes = ReadInt32("Ws Max Message Bytes", defaults.MaxMessageBytes),
            ConnectAttempts = ReadInt32("Ws Connect Attempts", defaults.ConnectAttempts),
        }.Validate();
    }

    private TimeSpan ReadSeconds(string keyword, TimeSpan defaultValue)
    {
        var raw = _builder.GetOption(keyword);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            throw new InvalidOperationException($"{keyword} must be a non-negative number of seconds.");

        return TimeSpan.FromSeconds(seconds);
    }

    private int ReadInt32(string keyword, int defaultValue)
    {
        var raw = _builder.GetOption(keyword);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"{keyword} must be an integer.");

        return value;
    }

    public static AhtolaConnectionOptions Parse(string connectionString)
    {
        return new AhtolaConnectionOptions(new AhtolaConnectionStringBuilder(connectionString));
    }

    internal bool TryGetManagedPoolKey(out ManagedConnectionPoolKey key)
    {
        key = default;
        if (!Pooling || IsRemote || LocalProvider != AhtolaLocalProvider.Managed)
            return false;

        var mode = ParseManagedOpenMode(Mode);
        var dataSource = string.IsNullOrEmpty(DataSource) ? ":memory:" : DataSource;
        if (mode == ManagedLocalOpenMode.Memory
            || dataSource.Equals(":memory:", StringComparison.Ordinal)
            || GetEncryptionCipher().HasValue
                    || _builder.GetOption("Encryption Key") is not null
                    || !string.IsNullOrWhiteSpace(_builder.GetOption("Password")))
        {
            return false;
        }

        key = ManagedConnectionPoolKey.Create(
            dataSource,
            mode == ManagedLocalOpenMode.ReadOnly);
        return true;
    }

    internal static AhtolaConnectionOptions FromReplica(AhtolaReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new AhtolaConnectionStringBuilder
        {
            DataSource = options.RemoteUri.AbsoluteUri,
            ReplicaPath = options.Path,
            LocalProvider = AhtolaLocalProvider.Managed,
        };
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            builder.AuthToken = options.AuthToken;
        builder.SyncInterval = options.SyncInterval;
        return new AhtolaConnectionOptions(builder);
    }

    private string ValidateTls(string scheme, bool expectedTls, string? normalizedScheme = null)
    {
        if (Tls.HasValue && Tls.Value != expectedTls)
        {
            var actual = Tls.Value.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException($"Tls={actual} conflicts with the {scheme} URL scheme.");
        }

        return normalizedScheme ?? scheme;
    }

    private static ManagedLocalOpenMode ParseManagedOpenMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)
            || mode.Equals("ReadWriteCreate", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("rwc", StringComparison.OrdinalIgnoreCase))
        {
            return ManagedLocalOpenMode.ReadWriteCreate;
        }

        if (mode.Equals("ReadWrite", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
        {
            return ManagedLocalOpenMode.ReadWrite;
        }

        if (mode.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("ro", StringComparison.OrdinalIgnoreCase))
        {
            return ManagedLocalOpenMode.ReadOnly;
        }

        if (mode.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            return ManagedLocalOpenMode.Memory;

        throw new ArgumentException($"Invalid Mode value for Local Provider=Managed: {mode}.", nameof(mode));
    }

    private ManagedEncryptionOptions? CreateManagedEncryptionOptions(
        ManagedLocalOpenMode mode,
        string dataSource)
    {
        var password = _builder.GetOption("Password");
        var hasPassword = !string.IsNullOrEmpty(password);
        var passwordScheme = _builder.GetOption("Password Scheme");
        var cipher = _builder.GetOption("Encryption Cipher");
        var key = _builder.GetOption("Encryption Key");
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

        ManagedEncryptionOptions? options;
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

            options = scheme.DeriveEncryptionOptions(password!);
        }
        else if (string.IsNullOrWhiteSpace(cipher))
        {
            if (key is not null)
            {
                throw new InvalidOperationException(
                    "Encryption Cipher is required when Encryption Key is specified.");
            }

            return null;
        }
        else if (!hasKey)
        {
            throw new InvalidOperationException("Encryption Key is required when Encryption Cipher is specified.");
        }
        else
        {
            options = cipher.ToLowerInvariant() switch
            {
                "aes128gcm" or "aes-128-gcm" or "aes_128_gcm" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes128Gcm,
                    key!),
                "aes256gcm" or "aes-256-gcm" or "aes_256_gcm" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
                    key!),
                "aegis256" or "aegis-256" or "aegis_256" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256,
                    key!),
                "aegis256x2" or "aegis-256x2" or "aegis_256x2" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256X2,
                    key!),
                "aegis256x4" or "aegis-256x4" or "aegis_256x4" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis256X4,
                    key!),
                "aegis128l" or "aegis-128l" or "aegis_128l" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128L,
                    key!),
                "aegis128x2" or "aegis-128x2" or "aegis_128x2" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128X2,
                    key!),
                "aegis128x4" or "aegis-128x4" or "aegis_128x4" => ManagedEncryptionOptions.FromHex(
                    Ahtola.Core.Storage.AhtolaEncryptionCipher.Aegis128X4,
                    key!),
                _ => throw new NotSupportedException(
                    "Local Provider=Managed supports only Ahtola encrypted format version 0 cipher IDs 1 through 8 "
                    + "(AES128GCM, AES256GCM, AEGIS256, AEGIS256X2, AEGIS256X4, AEGIS128L, AEGIS128X2, AEGIS128X4); "
                    + "cipher fallback is not permitted."),
            };
        }

        if (mode == ManagedLocalOpenMode.Memory || dataSource == ":memory:")
        {
            options.Dispose();
            throw new NotSupportedException(
                "Encryption is supported only for file-backed databases when Local Provider=Managed.");
        }

        return options;
    }

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
}

internal readonly record struct ManagedLocalOpenOptions(
string DataSource,
bool ReadOnly,
ManagedEncryptionOptions? Encryption,
string? SharedMemoryName,
bool ForeignReadOnly = false) : IDisposable
{
    public void Dispose() => Encryption?.Dispose();
}

internal static class ManagedSharedCacheContract
{
    public const string ReadUncommittedNotSupportedMessage =
        "PRAGMA read_uncommitted and IsolationLevel.ReadUncommitted are not supported for managed shared-memory databases because the managed engine preserves transaction isolation and does not expose dirty reads.";
}

internal enum ManagedLocalOpenMode
{
    ReadWriteCreate,
    ReadWrite,
    ReadOnly,
    Memory,
}
