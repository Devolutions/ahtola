using System.Security.Cryptography;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser;

/// <summary>
/// Immutable key material for a browser database encrypted with Ahtola's AHTLA
/// page format. Instances hold secrets, so they are never serialized into a
/// connection string and should be disposed once the owning data source has
/// been created.
/// </summary>
/// <remarks>
/// The browser derives and imports keys through Web Crypto, so the resulting
/// AES-GCM key never leaves the JavaScript realm as extractable material. The
/// on-disk bytes match the desktop <see cref="AhtolaEncryptionOptions"/> format
/// exactly, so a database written by one can be opened by the other.
/// </remarks>
public sealed class AhtolaBrowserEncryptionOptions : IDisposable
{
    private byte[]? _secret;

    private AhtolaBrowserEncryptionOptions(
        AhtolaEncryptionCipher cipher,
        byte[] secret,
        bool isPasswordDerived)
    {
        Cipher = cipher;
        _secret = secret;
        IsPasswordDerived = isPasswordDerived;
    }

    /// <summary>The AHTLA cipher id stored in the encrypted page 1 header.</summary>
    public AhtolaEncryptionCipher Cipher { get; }

    /// <summary>Whether the key is derived from a passphrase rather than supplied directly.</summary>
    public bool IsPasswordDerived { get; }

    /// <summary>
    /// The passphrase scheme used when <see cref="IsPasswordDerived"/> is true,
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? PasswordSchemeId
        => IsPasswordDerived ? AhtolaBrowserCryptoParameters.PasswordSchemeId : null;

    /// <summary>
    /// Derives an AES-256-GCM key from <paramref name="password"/> using
    /// <c>Ahtola.Password.v1</c> (PBKDF2-HMAC-SHA256, 210,000 iterations).
    /// </summary>
    public static AhtolaBrowserEncryptionOptions FromPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return new AhtolaBrowserEncryptionOptions(
            AhtolaEncryptionCipher.Aes256Gcm,
            Encoding.UTF8.GetBytes(password),
            isPasswordDerived: true);
    }

    /// <summary>Uses an exact key for any Ahtola format version 0 cipher.</summary>
    public static AhtolaBrowserEncryptionOptions FromKey(
        AhtolaEncryptionCipher cipher,
        ReadOnlySpan<byte> key)
    {
        var requiredKeySize = AhtolaBrowserCryptoParameters.GetKeySize(cipher);
        if (key.Length != requiredKeySize)
        {
            throw new ArgumentException(
                $"{cipher} requires a {requiredKeySize}-byte key, but the supplied key has {key.Length} bytes.",
                nameof(key));
        }

        return new AhtolaBrowserEncryptionOptions(cipher, key.ToArray(), isPasswordDerived: false);
    }

    /// <summary>Uses an exact key in Ahtola's hexadecimal representation.</summary>
    public static AhtolaBrowserEncryptionOptions FromHex(AhtolaEncryptionCipher cipher, string hexKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hexKey);

        byte[] key;
        try
        {
            key = Convert.FromHexString(hexKey.Trim());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Encryption keys must be hexadecimal.", nameof(hexKey), exception);
        }

        try
        {
            return FromKey(cipher, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Zeros this instance's copy of the passphrase or key material.</summary>
    public void Dispose()
    {
        var secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null)
            CryptographicOperations.ZeroMemory(secret);
    }

    internal AhtolaBrowserEncryptionOptions CreateOwnedCopy()
    {
        var secret = _secret ?? throw new ObjectDisposedException(nameof(AhtolaBrowserEncryptionOptions));
        return new AhtolaBrowserEncryptionOptions(Cipher, secret.AsSpan().ToArray(), IsPasswordDerived);
    }

    /// <summary>
    /// Creates the Web Crypto key handle for this configuration. The caller owns
    /// the returned service and must dispose it to release the JavaScript key.
    /// Only valid for the AES-GCM ciphers; AEGIS goes through
    /// <see cref="CreateManagedAegisPageCipher"/>.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    internal async ValueTask<AhtolaBrowserCryptoService> CreateCryptoServiceAsync()
    {
        var secret = _secret ?? throw new ObjectDisposedException(nameof(AhtolaBrowserEncryptionOptions));
        if (!AhtolaBrowserCryptoParameters.UsesWebCrypto(Cipher))
        {
            throw new InvalidOperationException(
                $"{Cipher} is not implemented by Web Crypto; use the managed AEGIS page cipher instead.");
        }

        if (!IsPasswordDerived)
            return await AhtolaBrowserCryptoService.CreateAsync(Cipher, secret).ConfigureAwait(false);

        // Web Crypto's PBKDF2 binding takes the passphrase as a string, so the
        // transient managed copy is unavoidable; it is not retained here.
        var password = Encoding.UTF8.GetString(secret);
        return await AhtolaBrowserCryptoService.CreateFromPasswordAsync(password).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the pure-managed AEGIS page cipher. Passphrase-derived keys always
    /// use AES-256-GCM, so this is only reachable with an explicit key.
    /// </summary>
    internal Storage.AhtolaManagedAegisPageCipher CreateManagedAegisPageCipher()
    {
        var secret = _secret ?? throw new ObjectDisposedException(nameof(AhtolaBrowserEncryptionOptions));
        if (IsPasswordDerived)
        {
            throw new InvalidOperationException(
                "Passphrase-derived browser keys use Ahtola.Password.v1, which produces an AES-256-GCM key.");
        }

        return new Storage.AhtolaManagedAegisPageCipher(
            AhtolaBrowserCryptoParameters.ToStorageCipher(Cipher),
            secret);
    }
}
