using System.Runtime.Versioning;
using System.Security.Cryptography;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser.Interop;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Provides AHTLA-compatible PBKDF2-HMAC-SHA256 and AES-GCM through browser Web Crypto.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class AhtolaBrowserCryptoService : IDisposable, IAsyncDisposable
{
    private int _keyHandle;

    private AhtolaBrowserCryptoService(AhtolaEncryptionCipher cipher, int keyHandle)
    {
        Cipher = cipher;
        _keyHandle = keyHandle;
    }

    /// <summary>The AHTLA AES-GCM cipher represented by this service.</summary>
    public AhtolaEncryptionCipher Cipher { get; }

    /// <summary>
    /// Derives a non-extractable AES-256-GCM key using <c>Ahtola.Password.v1</c>.
    /// </summary>
    public static async ValueTask<AhtolaBrowserCryptoService> CreateFromPasswordAsync(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        await AhtolaBrowserCryptoRuntime.InitializeAsync().ConfigureAwait(false);
        var handle = await BrowserCryptoInterop.CreatePasswordKeyAsync(
                password,
                AhtolaBrowserCryptoParameters.PasswordSalt,
                AhtolaBrowserCryptoParameters.PasswordIterations,
                AhtolaBrowserCryptoParameters.PasswordKeySize * 8)
            .ConfigureAwait(false);
        return new AhtolaBrowserCryptoService(AhtolaEncryptionCipher.Aes256Gcm, handle);
    }

    /// <summary>Imports an exact AES-128 or AES-256 key as a non-extractable Web Crypto key.</summary>
    public static async ValueTask<AhtolaBrowserCryptoService> CreateAsync(
        AhtolaEncryptionCipher cipher,
        ReadOnlyMemory<byte> key)
    {
        var requiredKeySize = AhtolaBrowserCryptoParameters.GetKeySize(cipher);
        if (key.Length != requiredKeySize)
        {
            throw new ArgumentException(
                $"{cipher} requires a {requiredKeySize}-byte key, but the supplied key has {key.Length} bytes.",
                nameof(key));
        }

        var keyCopy = key.ToArray();
        try
        {
            await AhtolaBrowserCryptoRuntime.InitializeAsync().ConfigureAwait(false);
            var handle = await BrowserCryptoInterop.ImportAesGcmKeyAsync(keyCopy).ConfigureAwait(false);
            return new AhtolaBrowserCryptoService(cipher, handle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    /// <summary>
    /// Derives the raw 32-byte <c>Ahtola.Password.v1</c> key for interoperability diagnostics.
    /// The caller owns and should clear the returned key.
    /// </summary>
    public static async ValueTask<byte[]> DerivePasswordKeyBytesAsync(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        await AhtolaBrowserCryptoRuntime.InitializeAsync().ConfigureAwait(false);
        using var result = await BrowserCryptoInterop.DerivePasswordBitsAsync(
                password,
                AhtolaBrowserCryptoParameters.PasswordSalt,
                AhtolaBrowserCryptoParameters.PasswordIterations,
                AhtolaBrowserCryptoParameters.PasswordKeySize * 8)
            .ConfigureAwait(false);
        var key = BrowserCryptoInterop.ConsumeByteArray(result);
        if (key.Length != AhtolaBrowserCryptoParameters.PasswordKeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("Web Crypto returned an invalid Ahtola.Password.v1 key length.");
        }

        return key;
    }

    /// <summary>Encrypts bytes with a caller-supplied AHTLA nonce and associated data.</summary>
    public async ValueTask<AhtolaBrowserAesGcmResult> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData = default)
    {
        ValidateNonce(nonce);
        var keyHandle = GetKeyHandle();
        var plaintextCopy = plaintext.ToArray();
        var nonceCopy = nonce.ToArray();
        var associatedDataCopy = associatedData.ToArray();
        try
        {
            using var result = await BrowserCryptoInterop.EncryptAesGcmAsync(
                    keyHandle,
                    nonceCopy,
                    plaintextCopy,
                    associatedDataCopy)
                .ConfigureAwait(false);
            var combined = BrowserCryptoInterop.ConsumeByteArray(result);
            try
            {
                if (combined.Length != plaintext.Length + AhtolaBrowserCryptoParameters.AesGcmTagSize)
                    throw new CryptographicException("Web Crypto returned an invalid AES-GCM ciphertext length.");

                var ciphertext = combined.AsSpan(0, plaintext.Length).ToArray();
                var tag = combined.AsSpan(plaintext.Length).ToArray();
                return new AhtolaBrowserAesGcmResult(ciphertext, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(combined);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextCopy);
            CryptographicOperations.ZeroMemory(nonceCopy);
            CryptographicOperations.ZeroMemory(associatedDataCopy);
        }
    }

    /// <summary>Authenticates and decrypts bytes using separate AHTLA tag, nonce, and associated data.</summary>
    public async ValueTask<byte[]> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData = default)
    {
        ValidateNonce(nonce);
        if (tag.Length != AhtolaBrowserCryptoParameters.AesGcmTagSize)
        {
            throw new ArgumentException(
                $"AHTLA AES-GCM tags must be exactly {AhtolaBrowserCryptoParameters.AesGcmTagSize} bytes.",
                nameof(tag));
        }

        var keyHandle = GetKeyHandle();
        var ciphertextCopy = ciphertext.ToArray();
        var tagCopy = tag.ToArray();
        var nonceCopy = nonce.ToArray();
        var associatedDataCopy = associatedData.ToArray();
        try
        {
            using var result = await BrowserCryptoInterop.DecryptAesGcmAsync(
                    keyHandle,
                    nonceCopy,
                    ciphertextCopy,
                    tagCopy,
                    associatedDataCopy)
                .ConfigureAwait(false);
            var plaintext = BrowserCryptoInterop.ConsumeByteArray(result);
            if (plaintext.Length != ciphertext.Length)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new CryptographicException("Web Crypto returned an invalid AES-GCM plaintext length.");
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertextCopy);
            CryptographicOperations.ZeroMemory(tagCopy);
            CryptographicOperations.ZeroMemory(nonceCopy);
            CryptographicOperations.ZeroMemory(associatedDataCopy);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var keyHandle = Interlocked.Exchange(ref _keyHandle, 0);
        if (keyHandle != 0)
            BrowserCryptoInterop.ReleaseKey(keyHandle);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private int GetKeyHandle()
    {
        ObjectDisposedException.ThrowIf(_keyHandle == 0, this);
        return _keyHandle;
    }

    private static void ValidateNonce(ReadOnlyMemory<byte> nonce)
    {
        if (nonce.Length != AhtolaBrowserCryptoParameters.AesGcmNonceSize)
        {
            throw new ArgumentException(
                $"AHTLA AES-GCM nonces must be exactly {AhtolaBrowserCryptoParameters.AesGcmNonceSize} bytes.",
                nameof(nonce));
        }
    }
}
