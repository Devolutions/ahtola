using System.Security.Cryptography;
using System.Text;

namespace Ahtola.Core.Storage;

/// <summary>
/// Built-in passphrase helpers and SDS-shaped open-failure phrasing.
/// </summary>
/// <remarks>
/// Prefer <see cref="AhtolaPassphraseSchemes"/> / <c>Password Scheme=</c> for
/// selection. <see cref="FromPassword(string)"/> remains the default-scheme
/// shortcut used by existing RDM connection strings.
/// </remarks>
public static class AhtolaPasswordEncryption
{
    /// <summary>Built-in v1 scheme id (also the default <c>Password Scheme</c>).</summary>
    public const string SchemeIdV1 = "Ahtola.Password.v1";

    /// <summary>Stable domain separation label for passphrase derivation v1.</summary>
    public const string DomainSaltV1 = SchemeIdV1;

    /// <summary>
    /// PBKDF2 iteration count for v1. Chosen as a modern floor; desktop open
    /// latency on large DBs may warrant a documented lower value later under a
    /// new scheme id — never change this constant for <see cref="SchemeIdV1"/>.
    /// </summary>
    public const int Pbkdf2IterationsV1 = 210_000;

    /// <summary>
    /// Substring RDM and SDS-shaped consumers look for when detecting a
    /// password-protected or non-database file.
    /// </summary>
    public const string EncryptedOrNotDatabaseMessage =
        "file is encrypted or is not a database";

    /// <summary>
    /// Derives AES-256-GCM options using the catalog default scheme
    /// (<see cref="SchemeIdV1"/> today). Equivalent to
    /// <c>FromPassword(password, schemeId: null)</c>.
    /// </summary>
    public static AhtolaEncryptionOptions FromPassword(string password)
        => FromPassword(password, schemeId: null);

    /// <summary>
    /// Derives encryption options via the registered
    /// <see cref="IAhtolaPassphraseScheme"/> identified by
    /// <paramref name="schemeId"/> (default scheme when null/empty).
    /// </summary>
    public static AhtolaEncryptionOptions FromPassword(string password, string? schemeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return AhtolaPassphraseSchemes.Resolve(schemeId).DeriveEncryptionOptions(password);
    }

    /// <summary>
    /// v1 KDF implementation. Prefer <see cref="FromPassword(string, string?)"/>
    /// so scheme selection stays centralized.
    /// </summary>
    internal static AhtolaEncryptionOptions DeriveV1(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var salt = Encoding.UTF8.GetBytes(DomainSaltV1);
            try
            {
                var key = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    salt,
                    Pbkdf2IterationsV1,
                    HashAlgorithmName.SHA256,
                    outputLength: 32);
                try
                {
                    return new AhtolaEncryptionOptions(AhtolaEncryptionCipher.Aes256Gcm, key);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="message"/> already carries the SDS-shaped
    /// encrypted-or-not-database detection phrase.
    /// </summary>
    public static bool ContainsEncryptedOrNotDatabasePhrase(string? message)
        => !string.IsNullOrEmpty(message)
           && message.Contains(EncryptedOrNotDatabaseMessage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures failure text includes the SDS-shaped detection phrase so RDM
    /// <c>IsPasswordProtected</c> / open sniffing keep working.
    /// The classic phrase is placed first so substring detectors and
    /// <c>StartsWith</c>-style checks stay reliable.
    /// </summary>
    public static string EnsureEncryptedOrNotDatabasePhrase(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return EncryptedOrNotDatabaseMessage;
        if (ContainsEncryptedOrNotDatabasePhrase(message))
            return message;
        return $"{EncryptedOrNotDatabaseMessage}: {message}";
    }
}
