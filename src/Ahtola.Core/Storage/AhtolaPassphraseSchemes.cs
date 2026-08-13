using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Ahtola.Core.Storage;

/// <summary>
/// Built-in and app-registered passphrase → AHTLA key-derivation schemes.
/// </summary>
/// <remarks>
/// AOT-safe: no reflection discovery. Built-ins are fixed; apps may
/// <see cref="Register"/> additional <see cref="IAhtolaPassphraseScheme"/>
/// instances for private recipes. Built-in ids cannot be replaced.
/// </remarks>
public static class AhtolaPassphraseSchemes
{
    /// <summary>Default scheme id used when <c>Password Scheme</c> is omitted.</summary>
    public const string DefaultSchemeId = AhtolaPasswordEncryption.SchemeIdV1;

    private static readonly ConcurrentDictionary<string, IAhtolaPassphraseScheme> Schemes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> BuiltInIds =
        new(StringComparer.OrdinalIgnoreCase) { AhtolaPasswordEncryption.SchemeIdV1 };

    static AhtolaPassphraseSchemes()
    {
        RegisterCore(new AhtolaPasswordV1PassphraseScheme());
    }

    /// <summary>The scheme used when connection strings omit <c>Password Scheme</c>.</summary>
    public static IAhtolaPassphraseScheme Default => GetRequired(DefaultSchemeId);

    /// <summary>Snapshot of currently registered scheme ids (built-in + app).</summary>
    public static IReadOnlyCollection<string> RegisteredIds
        => Schemes.Keys.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Returns true when a scheme with <paramref name="schemeId"/> is registered.</summary>
    public static bool IsRegistered(string schemeId)
        => !string.IsNullOrWhiteSpace(schemeId) && Schemes.ContainsKey(schemeId.Trim());

    /// <summary>Looks up a registered scheme by id.</summary>
    public static bool TryGet(string? schemeId, [NotNullWhen(true)] out IAhtolaPassphraseScheme? scheme)
    {
        scheme = null;
        if (string.IsNullOrWhiteSpace(schemeId))
            return false;
        return Schemes.TryGetValue(schemeId.Trim(), out scheme);
    }

    /// <summary>
    /// Resolves <paramref name="schemeId"/>, or <see cref="Default"/> when null/empty.
    /// Throws <see cref="NotSupportedException"/> for unknown ids.
    /// </summary>
    public static IAhtolaPassphraseScheme Resolve(string? schemeId)
    {
        if (string.IsNullOrWhiteSpace(schemeId))
            return Default;

        var id = schemeId.Trim();
        if (TryGet(id, out var scheme))
            return scheme;

        var known = string.Join(", ", RegisteredIds);
        throw new NotSupportedException(
            $"Unknown Password Scheme '{id}'. Registered schemes: {known}. "
            + "Passphrase schemes only derive Ahtola AHTLA page keys; "
            + "SEE/SQLCipher and other page layouts require a separate IPageCodec.");
    }

    /// <summary>Returns a registered scheme or throws.</summary>
    public static IAhtolaPassphraseScheme GetRequired(string schemeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemeId);
        return Resolve(schemeId);
    }

    /// <summary>
    /// Registers an application-defined passphrase scheme.
    /// Built-in ids cannot be overwritten. Returns false if the id already exists.
    /// </summary>
    public static bool Register(IAhtolaPassphraseScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme.Id);

        var id = scheme.Id.Trim();
        if (BuiltInIds.Contains(id))
        {
            throw new InvalidOperationException(
                $"Password Scheme '{id}' is a built-in Ahtola scheme and cannot be replaced.");
        }

        return Schemes.TryAdd(id, scheme);
    }

    /// <summary>
    /// Removes a previously <see cref="Register"/>ed app scheme.
    /// Built-ins cannot be removed. Returns false if missing or built-in.
    /// </summary>
    public static bool Unregister(string schemeId)
    {
        if (string.IsNullOrWhiteSpace(schemeId))
            return false;

        var id = schemeId.Trim();
        if (BuiltInIds.Contains(id))
            return false;

        return Schemes.TryRemove(id, out _);
    }

    private static void RegisterCore(IAhtolaPassphraseScheme scheme)
        => Schemes[scheme.Id] = scheme;

    private sealed class AhtolaPasswordV1PassphraseScheme : IAhtolaPassphraseScheme
    {
        public string Id => AhtolaPasswordEncryption.SchemeIdV1;

        public string Description =>
            "PBKDF2-HMAC-SHA256 with fixed domain salt Ahtola.Password.v1 (210000 iterations) → AES-256-GCM.";

        public AhtolaEncryptionCipher PageCipher => AhtolaEncryptionCipher.Aes256Gcm;

        public AhtolaEncryptionOptions DeriveEncryptionOptions(string password)
            => AhtolaPasswordEncryption.DeriveV1(password);
    }
}
