using System.Runtime.Versioning;
using System.Security.Cryptography;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Interop;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>Browser-facing known-answer diagnostics for Web Crypto integration tests.</summary>
[SupportedOSPlatform("browser")]
public static class AhtolaBrowserCryptoDiagnostics
{
    /// <summary>Runs PBKDF2, AES-128-GCM, AES-256-GCM, AAD, and key-release checks.</summary>
    public static async ValueTask<AhtolaBrowserCryptoDiagnosticResult> RunKnownAnswersAsync()
    {
        await AhtolaBrowserCryptoRuntime.InitializeAsync().ConfigureAwait(false);
        var retainedKeysBefore = BrowserCryptoInterop.GetRetainedKeyCount();

        var expectedPasswordKey = AhtolaBrowserCryptoKnownAnswers.GetPasswordKey();
        var actualPasswordKey = await AhtolaBrowserCryptoService
            .DerivePasswordKeyBytesAsync(AhtolaBrowserCryptoKnownAnswers.Password)
            .ConfigureAwait(false);
        bool passwordMatches;
        try
        {
            passwordMatches = CryptographicOperations.FixedTimeEquals(
                expectedPasswordKey,
                actualPasswordKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedPasswordKey);
            CryptographicOperations.ZeroMemory(actualPasswordKey);
        }

        bool aes128Matches;
        using (var vector = AhtolaBrowserCryptoKnownAnswers.GetAes128())
        {
            aes128Matches = await VerifyAesGcmAsync(
                    AhtolaEncryptionCipher.Aes128Gcm,
                    vector)
                .ConfigureAwait(false);
        }

        bool aes256Matches;
        using (var vector = AhtolaBrowserCryptoKnownAnswers.GetAes256())
        {
            aes256Matches = await VerifyAesGcmAsync(
                    AhtolaEncryptionCipher.Aes256Gcm,
                    vector)
                .ConfigureAwait(false);
        }

        var retainedKeysAfter = BrowserCryptoInterop.GetRetainedKeyCount();
        return new AhtolaBrowserCryptoDiagnosticResult(
            passwordMatches,
            aes128Matches,
            aes256Matches,
            retainedKeysBefore,
            retainedKeysAfter);
    }

    private static async ValueTask<bool> VerifyAesGcmAsync(
        AhtolaEncryptionCipher cipher,
        AhtolaBrowserAesGcmKnownAnswer vector)
    {
        await using var service = await AhtolaBrowserCryptoService
            .CreateAsync(cipher, vector.Key)
            .ConfigureAwait(false);
        var encrypted = await service
            .EncryptAsync(
                vector.Plaintext,
                vector.Nonce,
                vector.AssociatedData)
            .ConfigureAwait(false);
        byte[]? decrypted = null;
        try
        {
            var ciphertextMatches = CryptographicOperations.FixedTimeEquals(
                encrypted.Ciphertext,
                vector.Ciphertext);
            var tagMatches = CryptographicOperations.FixedTimeEquals(encrypted.Tag, vector.Tag);
            decrypted = await service
                .DecryptAsync(
                    encrypted.Ciphertext,
                    encrypted.Tag,
                    vector.Nonce,
                    vector.AssociatedData)
                .ConfigureAwait(false);
            var plaintextMatches = CryptographicOperations.FixedTimeEquals(
                decrypted,
                vector.Plaintext);
            var tamperedTag = encrypted.Tag.ToArray();
            try
            {
                tamperedTag[0] ^= 0x80;
                try
                {
                    var unexpected = await service
                        .DecryptAsync(
                            encrypted.Ciphertext,
                            tamperedTag,
                            vector.Nonce,
                            vector.AssociatedData)
                        .ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(unexpected);
                    return false;
                }
                catch (AuthenticationTagMismatchException)
                {
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tamperedTag);
            }

            return ciphertextMatches && tagMatches && plaintextMatches;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
            CryptographicOperations.ZeroMemory(encrypted.Tag);
            if (decrypted is not null)
                CryptographicOperations.ZeroMemory(decrypted);
        }
    }
}

/// <summary>Results from the browser Web Crypto known-answer diagnostic.</summary>
public readonly record struct AhtolaBrowserCryptoDiagnosticResult(
    bool PasswordV1Matches,
    bool Aes128GcmMatches,
    bool Aes256GcmMatches,
    int RetainedKeysBefore,
    int RetainedKeysAfter)
{
    /// <summary>Whether all vectors matched and the diagnostic released every key it created.</summary>
    public bool Succeeded =>
        PasswordV1Matches
        && Aes128GcmMatches
        && Aes256GcmMatches
        && RetainedKeysAfter == RetainedKeysBefore;
}
