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

    /// <summary>The authentication tag size stored in every AHTLA AES-GCM page.</summary>
    public const int AesGcmTagSize = 16;

    internal static int GetKeySize(AhtolaEncryptionCipher cipher)
        => cipher switch
        {
            AhtolaEncryptionCipher.Aes128Gcm => 16,
            AhtolaEncryptionCipher.Aes256Gcm => 32,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                "Browser Web Crypto supports only AHTLA AES-128-GCM and AES-256-GCM."),
        };
}
