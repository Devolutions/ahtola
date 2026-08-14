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
            "aes128gcm" => AhtolaEncryptionCipher.Aes128Gcm,
            "aes256gcm" => AhtolaEncryptionCipher.Aes256Gcm,
            "aegis256" => AhtolaEncryptionCipher.Aegis256,
            "aegis256x2" => AhtolaEncryptionCipher.Aegis256x2,
            "aegis128l" => AhtolaEncryptionCipher.Aegis128l,
            "aegis128x2" => AhtolaEncryptionCipher.Aegis128x2,
            "aegis128x4" => AhtolaEncryptionCipher.Aegis128x4,
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
