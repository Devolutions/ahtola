using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>Stable Web Crypto parameters used by Ahtola encrypted pages.</summary>
public static class AhtolaBrowserCryptoParameters
{
    /// <summary>The passphrase scheme implemented by the browser crypto service.</summary>
    public const string PasswordSchemeId = AhtolaPasswordEncryption.SchemeIdV1;

    /// <summary>The UTF-8 domain salt for <see cref="PasswordSchemeId"/>.</summary>
    public const string PasswordSalt = AhtolaPasswordEncryption.DomainSaltV1;

    /// <summary>The PBKDF2 iteration count for <see cref="PasswordSchemeId"/>.</summary>
    public const int PasswordIterations = AhtolaPasswordEncryption.Pbkdf2IterationsV1;

    /// <summary>The AES-256 key size produced by <see cref="PasswordSchemeId"/>.</summary>
    public const int PasswordKeySize = 32;

    /// <summary>The nonce size stored in every AHTLA AES-GCM page.</summary>
    public const int AesGcmNonceSize = 12;

    /// <summary>The authentication tag size stored in every AHTLA page.</summary>
    public const int AesGcmTagSize = 16;

    /// <summary>
    /// Whether <paramref name="cipher"/> is served by Web Crypto. SubtleCrypto
    /// implements AES-GCM only, so every AEGIS variant runs through the
    /// pure-managed core instead.
    /// </summary>
    internal static bool UsesWebCrypto(AhtolaEncryptionCipher cipher)
        => cipher is AhtolaEncryptionCipher.Aes128Gcm or AhtolaEncryptionCipher.Aes256Gcm;

    internal static int GetKeySize(AhtolaEncryptionCipher cipher)
        => Core.Storage.AhtolaEncryptedPageFormat.GetParameters(ToStorageCipher(cipher)).KeySize;

    /// <summary>
    /// Maps the provider-level cipher enum onto the storage cipher whose numeric
    /// value is written into the AHTLA page 1 header. The two enums do not share
    /// numeric values, so this must always convert explicitly.
    /// </summary>
    internal static Core.Storage.AhtolaEncryptionCipher ToStorageCipher(AhtolaEncryptionCipher cipher)
        => cipher switch
        {
            AhtolaEncryptionCipher.Aes128Gcm => Core.Storage.AhtolaEncryptionCipher.Aes128Gcm,
            AhtolaEncryptionCipher.Aes256Gcm => Core.Storage.AhtolaEncryptionCipher.Aes256Gcm,
            AhtolaEncryptionCipher.Aegis256 => Core.Storage.AhtolaEncryptionCipher.Aegis256,
            AhtolaEncryptionCipher.Aegis256x2 => Core.Storage.AhtolaEncryptionCipher.Aegis256X2,
            AhtolaEncryptionCipher.Aegis256x4 => Core.Storage.AhtolaEncryptionCipher.Aegis256X4,
            AhtolaEncryptionCipher.Aegis128l => Core.Storage.AhtolaEncryptionCipher.Aegis128L,
            AhtolaEncryptionCipher.Aegis128x2 => Core.Storage.AhtolaEncryptionCipher.Aegis128X2,
            AhtolaEncryptionCipher.Aegis128x4 => Core.Storage.AhtolaEncryptionCipher.Aegis128X4,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                "The browser package supports Ahtola format version 0 cipher IDs 1 through 8 only; "
                + "ChaCha20-Poly1305 is a Turso Cloud server-side cipher with no on-disk cipher id."),
        };
}
