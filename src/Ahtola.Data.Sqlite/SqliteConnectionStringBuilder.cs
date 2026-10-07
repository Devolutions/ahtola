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
        "Sync Client Name",
        "Sync Long Poll Timeout",
        "Bootstrap If Empty",
        "Partial Bootstrap Prefix",
        "Partial Bootstrap Query",
        "Partial Sync Segment Size",
        "Partial Sync Prefetch",
        "Remote Encryption Cipher",
        "Remote Encryption Key",
        "Push Operations Threshold",
        "Pull Bytes Threshold",
        "Force Logical MVCC Pull",
        "Sync Experimental Features",
        "Automatic Sync Mode",
        "Tls",
        "Ws Keepalive Interval",
        "Ws Keepalive Timeout",
        "Ws Half Open Timeout",
        "Ws Max Message Bytes",
        "Ws Connect Attempts",
        "Journal Mode",
        "Synchronous",
        "Read Only",
        "FailIfMissing",
        "Page Size",
        "Cache Size",
        "Busy Timeout",
        "Legacy Format",
        "Password",
        "Ignore Unknown Keywords",
        "Auto Enlist Transaction",
        "Type Mapping",
        "Guid Column Name Heuristic",
    ];

    private static readonly Dictionary<string, string> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Data Source"] = "Data Source",
        ["DataSource"] = "Data Source",
        ["Filename"] = "Data Source",
        ["Mode"] = "Mode",
        ["Cache"] = "Cache",
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
        // Advanced embedded-replica options, named as in Turso's SqliteConnectionStringBuilder.
        ["Sync Client Name"] = "Sync Client Name",
        ["SyncClientName"] = "Sync Client Name",
        ["Sync Long Poll Timeout"] = "Sync Long Poll Timeout",
        ["SyncLongPollTimeout"] = "Sync Long Poll Timeout",
        ["Bootstrap If Empty"] = "Bootstrap If Empty",
        ["BootstrapIfEmpty"] = "Bootstrap If Empty",
        ["Partial Bootstrap Prefix"] = "Partial Bootstrap Prefix",
        ["PartialBootstrapPrefix"] = "Partial Bootstrap Prefix",
        ["Partial Bootstrap Query"] = "Partial Bootstrap Query",
        ["PartialBootstrapQuery"] = "Partial Bootstrap Query",
        ["Partial Sync Segment Size"] = "Partial Sync Segment Size",
        ["PartialSyncSegmentSize"] = "Partial Sync Segment Size",
        ["Partial Sync Prefetch"] = "Partial Sync Prefetch",
        ["PartialSyncPrefetch"] = "Partial Sync Prefetch",
        ["Remote Encryption Cipher"] = "Remote Encryption Cipher",
        ["RemoteEncryptionCipher"] = "Remote Encryption Cipher",
        ["Remote Encryption Key"] = "Remote Encryption Key",
        ["RemoteEncryptionKey"] = "Remote Encryption Key",
        ["Push Operations Threshold"] = "Push Operations Threshold",
        ["PushOperationsThreshold"] = "Push Operations Threshold",
        ["Pull Bytes Threshold"] = "Pull Bytes Threshold",
        ["PullBytesThreshold"] = "Pull Bytes Threshold",
        ["Force Logical MVCC Pull"] = "Force Logical MVCC Pull",
        ["ForceLogicalMvccPull"] = "Force Logical MVCC Pull",
        ["Sync Experimental Features"] = "Sync Experimental Features",
        ["SyncExperimentalFeatures"] = "Sync Experimental Features",
        ["Automatic Sync Mode"] = "Automatic Sync Mode",
        ["AutomaticSyncMode"] = "Automatic Sync Mode",
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
        // System.Data.SQLite keywords. RDM and other System.Data.SQLite consumers persist
        // connection strings that use them, so they are accepted and translated into pragmas
        // or open modes rather than rejected.
        ["Journal Mode"] = "Journal Mode",
        ["JournalMode"] = "Journal Mode",
        ["Synchronous"] = "Synchronous",
        ["Read Only"] = "Read Only",
        ["ReadOnly"] = "Read Only",
        ["FailIfMissing"] = "FailIfMissing",
        ["Fail If Missing"] = "FailIfMissing",
        ["Page Size"] = "Page Size",
        ["PageSize"] = "Page Size",
        ["Cache Size"] = "Cache Size",
        ["CacheSize"] = "Cache Size",
        ["Busy Timeout"] = "Busy Timeout",
        ["BusyTimeout"] = "Busy Timeout",
        ["Legacy Format"] = "Legacy Format",
        ["LegacyFormat"] = "Legacy Format",
        ["Password"] = "Password",
        ["Pwd"] = "Password",
        ["Ignore Unknown Keywords"] = "Ignore Unknown Keywords",
        ["IgnoreUnknownKeywords"] = "Ignore Unknown Keywords",
        ["Auto Enlist Transaction"] = "Auto Enlist Transaction",
        ["AutoEnlistTransaction"] = "Auto Enlist Transaction",
        ["Type Mapping"] = "Type Mapping",
        ["TypeMapping"] = "Type Mapping",
        ["Guid Column Name Heuristic"] = "Guid Column Name Heuristic",
        ["GuidColumnNameHeuristic"] = "Guid Column Name Heuristic",
    };

    // Keywords a connection string carried that this provider does not know, kept only when
    // Ignore Unknown Keywords is on (System.Data.SQLite ignores unknown keywords).
    private readonly Dictionary<string, object?> _ignoredKeywords = new(StringComparer.OrdinalIgnoreCase);
    private bool _ignoreUnknownKeywords;

    public SqliteConnectionStringBuilder()
    {
    }

    public SqliteConnectionStringBuilder(string? connectionString)
    {
        // Honour Ignore Unknown Keywords wherever it appears in the string, not only when it
        // precedes the unknown keywords.
        _ignoreUnknownKeywords = RequestsIgnoreUnknownKeywords(connectionString);
        ConnectionString = connectionString ?? string.Empty;
    }

    private static bool RequestsIgnoreUnknownKeywords(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        var generic = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var keyword in new[] { "Ignore Unknown Keywords", "IgnoreUnknownKeywords" })
        {
            if (generic.TryGetValue(keyword, out var value)
                && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var enabled))
            {
                return enabled;
            }
        }

        return false;
    }

    /// <summary>
    /// System.Data.SQLite compatibility: when true, keywords this provider does not know are
    /// ignored instead of throwing <see cref="ArgumentException"/>. The setting applies wherever
    /// it appears in a connection string passed to the constructor or a connection.
    /// </summary>
    public bool IgnoreUnknownKeywords
    {
        get => GetBool("Ignore Unknown Keywords");
        set => this["Ignore Unknown Keywords"] = value;
    }

    /// <summary>Keywords that were ignored because <see cref="IgnoreUnknownKeywords"/> is on.</summary>
    public IReadOnlyDictionary<string, object?> IgnoredKeywords => _ignoredKeywords;

    /// <summary>
    /// System.Data.SQLite <c>Journal Mode</c>: the journal mode applied on every open
    /// (<c>Delete</c>, <c>Wal</c>, <c>Truncate</c>, <c>Persist</c>, <c>Memory</c> or
    /// <c>Off</c>). A database created by the open is created directly in that mode, so
    /// <c>Delete</c> never leaves WAL sidecar files behind. Empty keeps the database's mode
    /// (new databases are created in WAL mode).
    /// </summary>
    public string JournalMode
    {
        get => GetString("Journal Mode");
        set => SetString("Journal Mode", value);
    }

    /// <summary>System.Data.SQLite <c>Synchronous</c>: applied as <c>PRAGMA synchronous</c> on open.</summary>
    public string Synchronous
    {
        get => GetString("Synchronous");
        set => SetString("Synchronous", value);
    }

    /// <summary>System.Data.SQLite <c>Read Only</c>: opens read-only when <see cref="Mode"/> is not set.</summary>
    public bool ReadOnly
    {
        get => GetBool("Read Only");
        set => this["Read Only"] = value;
    }

    /// <summary>
    /// System.Data.SQLite <c>FailIfMissing</c>: opens read-write without creating the file when
    /// <see cref="Mode"/> is not set.
    /// </summary>
    public bool FailIfMissing
    {
        get => GetBool("FailIfMissing");
        set => this["FailIfMissing"] = value;
    }

    /// <summary>System.Data.SQLite <c>Page Size</c>: applied as <c>PRAGMA page_size</c> on open.</summary>
    public int? PageSize
    {
        get => GetNullableInt("Page Size");
        set => SetNullable("Page Size", value);
    }

    /// <summary>System.Data.SQLite <c>Cache Size</c>: applied as <c>PRAGMA cache_size</c> on open.</summary>
    public int? CacheSize
    {
        get => GetNullableInt("Cache Size");
        set => SetNullable("Cache Size", value);
    }

    /// <summary>
    /// System.Data.SQLite <c>BusyTimeout</c> in milliseconds: applied as
    /// <c>PRAGMA busy_timeout</c> on open, so it overrides <see cref="DefaultTimeout"/> for lock
    /// waits.
    /// </summary>
    public int? BusyTimeout
    {
        get => GetNullableInt("Busy Timeout");
        set => SetNullable("Busy Timeout", value);
    }

    /// <summary>System.Data.SQLite <c>Legacy Format</c>: accepted and ignored.</summary>
    public bool LegacyFormat
    {
        get => GetBool("Legacy Format");
        set => this["Legacy Format"] = value;
    }

    /// <summary>
    /// System.Data.SQLite <c>Password</c>: opens (or creates) the database with
    /// System.Data.SQLite's legacy RC4 page encryption
    /// (<c>Ahtola.Data.Sqlite.Codecs.SystemDataSQLiteRc4PageCodec</c>) unless a
    /// <see cref="SqliteConnection.PageCodec"/> is set explicitly.
    /// </summary>
    public string Password
    {
        get => GetString("Password");
        set => SetString("Password", value);
    }

    /// <summary>
    /// System.Data.SQLite compatibility: when true, commands that do not set
    /// <see cref="SqliteCommand.Transaction"/> run inside the connection's pending transaction, as
    /// in System.Data.SQLite, instead of failing as in Microsoft.Data.Sqlite (the default).
    /// </summary>
    public bool AutoEnlistTransaction
    {
        get => GetBool("Auto Enlist Transaction");
        set => this["Auto Enlist Transaction"] = value;
    }

    /// <summary>
    /// How declared column types map to CLR types in readers. <see cref="SqliteTypeMapping.SystemDataSQLite"/>
    /// reproduces System.Data.SQLite (<c>INT</c> as <see cref="int"/>, <c>BOOLEAN</c> as
    /// <see cref="bool"/>, <c>DATETIME</c> as <see cref="System.DateTime"/>, …).
    /// </summary>
    public SqliteTypeMapping TypeMapping
    {
        get => GetEnum("Type Mapping", SqliteTypeMapping.Default);
        set => this["Type Mapping"] = value;
    }

    /// <summary>
    /// Whether a 16-byte BLOB in a column named <c>ID</c> or <c>*ID</c> (declared TEXT, BLOB or
    /// with no type) reads back as a GUID string when <see cref="BinaryGUID"/> is on. Off by
    /// default; the heuristic turned hex ids into dashed GUID text for consumers that never
    /// declared their columns as GUIDs.
    /// </summary>
    public bool GuidColumnNameHeuristic
    {
        get => GetBool("Guid Column Name Heuristic");
        set => this["Guid Column Name Heuristic"] = value;
    }

    public string DataSource
    {
        get => GetString("Data Source");
        set => SetString("Data Source", value);
    }

    public SqliteOpenMode Mode
    {
        get
        {
            if (base.ContainsKey("Mode"))
                return GetEnum("Mode", SqliteOpenMode.ReadWriteCreate);
            // System.Data.SQLite spells the open mode with Read Only and FailIfMissing.
            if (GetBool("Read Only"))
                return SqliteOpenMode.ReadOnly;
            if (GetBool("FailIfMissing"))
                return SqliteOpenMode.ReadWrite;
            return SqliteOpenMode.ReadWriteCreate;
        }
        set => this["Mode"] = value;
    }

    public SqliteCacheMode Cache
    {
        get => GetEnum("Cache", SqliteCacheMode.Default);
        set => this["Cache"] = value;
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

    /// <summary>
    /// The local provider. When the keyword is absent this reports the provider a connection
    /// actually uses, <see cref="AhtolaLocalProvider.Managed"/>.
    /// </summary>
    public AhtolaLocalProvider LocalProvider
    {
        get => GetEnum("Local Provider", AhtolaLocalProvider.Managed);
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

    /// <summary>Client name prefixing the sync client identifier of a new embedded replica.</summary>
    public string SyncClientName
    {
        get => GetString("Sync Client Name");
        set => SetString("Sync Client Name", value);
    }

    /// <summary>Embedded-replica pull long-poll timeout in milliseconds; 0 disables long polling.</summary>
    public int SyncLongPollTimeout
    {
        get => GetInt("Sync Long Poll Timeout", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Sync Long Poll Timeout"] = value;
        }
    }

    /// <summary>Whether a missing embedded replica is bootstrapped from the remote (default true).</summary>
    public bool BootstrapIfEmpty
    {
        get => GetBool("Bootstrap If Empty", true);
        set => this["Bootstrap If Empty"] = value;
    }

    /// <summary>Byte prefix of a partial embedded-replica bootstrap; 0 disables it.</summary>
    public int PartialBootstrapPrefix
    {
        get => GetInt("Partial Bootstrap Prefix", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Partial Bootstrap Prefix"] = value;
        }
    }

    /// <summary>Server-side query selecting the pages of a partial embedded-replica bootstrap.</summary>
    public string PartialBootstrapQuery
    {
        get => GetString("Partial Bootstrap Query");
        set => SetString("Partial Bootstrap Query", value);
    }

    /// <summary>Lazy-loading segment size in bytes of a partial bootstrap; 0 uses the default.</summary>
    public long PartialSyncSegmentSize
    {
        get => GetLong("Partial Sync Segment Size", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Partial Sync Segment Size"] = value;
        }
    }

    /// <summary>Whether a partial bootstrap prefetches adjacent pages during lazy loading.</summary>
    public bool PartialSyncPrefetch
    {
        get => GetBool("Partial Sync Prefetch");
        set => this["Partial Sync Prefetch"] = value;
    }

    /// <summary>Cipher of an encrypted remote database used by an embedded replica (e.g. <c>aes256gcm</c>).</summary>
    public string RemoteEncryptionCipher
    {
        get => GetString("Remote Encryption Cipher");
        set => SetString("Remote Encryption Cipher", value);
    }

    /// <summary>Base64-encoded key of an encrypted remote database used by an embedded replica.</summary>
    public string RemoteEncryptionKey
    {
        get => GetString("Remote Encryption Key");
        set => SetString("Remote Encryption Key", value);
    }

    /// <summary>Maximum change operations per embedded-replica push batch; 0 uses the default.</summary>
    public long PushOperationsThreshold
    {
        get => GetLong("Push Operations Threshold", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Push Operations Threshold"] = value;
        }
    }

    /// <summary>Target size in bytes of each initial bootstrap pull; 0 pulls the whole image.</summary>
    public long PullBytesThreshold
    {
        get => GetLong("Pull Bytes Threshold", 0);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Pull Bytes Threshold"] = value;
        }
    }

    /// <summary>
    /// Turso's forced MVCC logical-pull escape hatch. The managed provider always auto-detects
    /// the pull protocol, so <see langword="true"/> fails closed when the connection opens.
    /// </summary>
    public bool ForceLogicalMvccPull
    {
        get => GetBool("Force Logical MVCC Pull");
        set => this["Force Logical MVCC Pull"] = value;
    }

    /// <summary>
    /// Turso's experimental-feature list for the replica's local database; validated, but it
    /// has no effect on the managed engine.
    /// </summary>
    public string SyncExperimentalFeatures
    {
        get => GetString("Sync Experimental Features");
        set => SetString("Sync Experimental Features", value);
    }

    /// <summary>
    /// What each <see cref="SyncInterval"/> tick does: <see cref="AhtolaAutomaticSyncMode.PushAndPull"/>
    /// (the default) or Turso's <see cref="AhtolaAutomaticSyncMode.PullOnly"/>.
    /// </summary>
    public AhtolaAutomaticSyncMode AutomaticSyncMode
    {
        get => GetEnum("Automatic Sync Mode", AhtolaAutomaticSyncMode.PushAndPull);
        set => this["Automatic Sync Mode"] = value;
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
            ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
            if (!KeywordMap.ContainsKey(keyword) && _ignoreUnknownKeywords)
            {
                _ignoredKeywords[keyword] = value;
                return;
            }

            var normalizedKeyword = NormalizeKeyword(keyword);
            if (value is null)
            {
                Remove(normalizedKeyword);
                if (normalizedKeyword == "Ignore Unknown Keywords")
                    _ignoreUnknownKeywords = false;
                return;
            }

            var stored = ConvertToStoredValue(normalizedKeyword, value);
            base[normalizedKeyword] = stored;
            if (normalizedKeyword == "Ignore Unknown Keywords")
                _ignoreUnknownKeywords = stored is true;
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
        if (base.ContainsKey("Sync Client Name"))
            builder["Sync Client Name"] = SyncClientName;
        if (base.ContainsKey("Sync Long Poll Timeout"))
            builder["Sync Long Poll Timeout"] = SyncLongPollTimeout;
        if (base.ContainsKey("Bootstrap If Empty"))
            builder["Bootstrap If Empty"] = BootstrapIfEmpty;
        if (base.ContainsKey("Partial Bootstrap Prefix"))
            builder["Partial Bootstrap Prefix"] = PartialBootstrapPrefix;
        if (base.ContainsKey("Partial Bootstrap Query"))
            builder["Partial Bootstrap Query"] = PartialBootstrapQuery;
        if (base.ContainsKey("Partial Sync Segment Size"))
            builder["Partial Sync Segment Size"] = PartialSyncSegmentSize;
        if (base.ContainsKey("Partial Sync Prefetch"))
            builder["Partial Sync Prefetch"] = PartialSyncPrefetch;
        if (base.ContainsKey("Remote Encryption Cipher"))
            builder["Remote Encryption Cipher"] = RemoteEncryptionCipher;
        if (base.ContainsKey("Remote Encryption Key"))
            builder["Remote Encryption Key"] = RemoteEncryptionKey;
        if (base.ContainsKey("Push Operations Threshold"))
            builder["Push Operations Threshold"] = PushOperationsThreshold;
        if (base.ContainsKey("Pull Bytes Threshold"))
            builder["Pull Bytes Threshold"] = PullBytesThreshold;
        if (base.ContainsKey("Force Logical MVCC Pull"))
            builder["Force Logical MVCC Pull"] = ForceLogicalMvccPull;
        if (base.ContainsKey("Sync Experimental Features"))
            builder["Sync Experimental Features"] = SyncExperimentalFeatures;
        if (base.ContainsKey("Automatic Sync Mode"))
            builder["Automatic Sync Mode"] = AutomaticSyncMode.ToString();
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
        if (base.ContainsKey("Local Provider"))
            builder["Local Provider"] = LocalProvider.ToString();
        else if (base.ContainsKey("Replica Path"))
            builder["Local Provider"] = AhtolaLocalProvider.Managed.ToString();

        return builder.ConnectionString;
    }

    internal AhtolaEncryptionOptions? CreateManagedEncryptionOptions()
    {
        var cipher = GetString("Encryption Cipher");
        var keyConfigured = base.TryGetValue("Encryption Key", out var keyValue);
        var key = keyConfigured
            ? Convert.ToString(keyValue, CultureInfo.InvariantCulture)
            : null;
        var hasKey = !string.IsNullOrWhiteSpace(key);

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
           || base.ContainsKey("Encryption Key");

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

    private int? GetNullableInt(string keyword)
    {
        return base.TryGetValue(keyword, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : null;
    }

    private long GetLong(string keyword, long defaultValue)
    {
        return base.TryGetValue(keyword, out var value)
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
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
            "Recursive Triggers" or "Pooling" or "BinaryGUID" or "Foreign Read Only" or "Read Your Writes"
                or "Bootstrap If Empty" or "Partial Sync Prefetch" or "Force Logical MVCC Pull"
                or "Read Only" or "FailIfMissing" or "Legacy Format" or "Ignore Unknown Keywords"
                or "Auto Enlist Transaction" or "Guid Column Name Heuristic"
                => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "Page Size" or "Cache Size" or "Busy Timeout"
                => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            "Type Mapping" => ConvertEnum<SqliteTypeMapping>(value),
            "Journal Mode" => ConvertJournalMode(value),
            "Synchronous" => ConvertSynchronous(value),
            "Tls" => ConvertToNullableBoolean(value),
            "Default Timeout" or "Version" or "Sync Interval" or "Ws Keepalive Interval" or "Ws Keepalive Timeout"
                or "Ws Half Open Timeout" or "Ws Max Message Bytes" or "Ws Connect Attempts"
                or "Sync Long Poll Timeout" or "Partial Bootstrap Prefix"
                => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            "Partial Sync Segment Size" or "Push Operations Threshold" or "Pull Bytes Threshold"
                => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            "DateTimeKind" => ConvertDateTimeKind(value),
            "Local Provider" => ConvertLocalProvider(value),
            "Automatic Sync Mode" => ConvertAutomaticSyncMode(value),
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
            "Automatic Sync Mode" => ConvertAutomaticSyncMode(value),
            "Type Mapping" => ConvertEnum<SqliteTypeMapping>(value),
            _ => value,
        };
    }

    private static readonly string[] JournalModes = ["Delete", "Truncate", "Persist", "Memory", "Wal", "Off", "Default"];

    private static readonly string[] SynchronousModes = ["Off", "Normal", "Full", "Extra", "0", "1", "2", "3"];

    private static string ConvertJournalMode(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (text.Length != 0 && !JournalModes.Contains(text, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid Journal Mode '{text}'. Expected one of: {string.Join(", ", JournalModes)}.");
        return text;
    }

    private static string ConvertSynchronous(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        if (text.Length != 0 && !SynchronousModes.Contains(text, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid Synchronous mode '{text}'. Expected Off, Normal, Full or Extra.");
        return text;
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
            "Local Provider" => AhtolaLocalProvider.Managed,
            "Foreign Read Only" => false,
            "Auth Token" => string.Empty,
            "Replica Path" => string.Empty,
            "Read Your Writes" => true,
            "Sync Interval" => 0,
            "Sync Client Name" => string.Empty,
            "Sync Long Poll Timeout" => 0,
            "Bootstrap If Empty" => true,
            "Partial Bootstrap Prefix" => 0,
            "Partial Bootstrap Query" => string.Empty,
            "Partial Sync Segment Size" => 0L,
            "Partial Sync Prefetch" => false,
            "Remote Encryption Cipher" => string.Empty,
            "Remote Encryption Key" => string.Empty,
            "Push Operations Threshold" => 0L,
            "Pull Bytes Threshold" => 0L,
            "Force Logical MVCC Pull" => false,
            "Sync Experimental Features" => string.Empty,
            "Automatic Sync Mode" => AhtolaAutomaticSyncMode.PushAndPull,
            "Tls" => null!,
            "Ws Keepalive Interval" => 30,
            "Ws Keepalive Timeout" => 20,
            "Ws Half Open Timeout" => 0,
            "Ws Max Message Bytes" => 16 * 1024 * 1024,
            "Ws Connect Attempts" => 3,
            "Journal Mode" => string.Empty,
            "Synchronous" => string.Empty,
            "Read Only" => false,
            "FailIfMissing" => false,
            "Page Size" => null!,
            "Cache Size" => null!,
            "Busy Timeout" => null!,
            "Legacy Format" => false,
            "Ignore Unknown Keywords" => false,
            "Auto Enlist Transaction" => false,
            "Type Mapping" => SqliteTypeMapping.Default,
            "Guid Column Name Heuristic" => false,
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

    private static AhtolaAutomaticSyncMode ConvertAutomaticSyncMode(object value)
    {
        var mode = ConvertEnum<AhtolaAutomaticSyncMode>(value);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                Properties.Resources.InvalidEnumValue(typeof(AhtolaAutomaticSyncMode), mode));

        return mode;
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
