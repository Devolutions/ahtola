using System.Security.Cryptography;

namespace Ahtola.Core.Storage.Crypto;

/// <summary>
/// The detached-tag AEAD primitive an encrypted Ahtola page is built from.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be safe for concurrent use: a single
/// <see cref="EncryptionPageCodec"/> is shared by every pager thread, so
/// <see cref="Encrypt"/> and <see cref="TryDecrypt"/> may run in parallel on the
/// same instance. Implementations therefore keep no mutable per-operation state
/// on the object.
/// </para>
/// <para>
/// <see cref="TryDecrypt"/> reports authentication failure by returning
/// <see langword="false"/> rather than throwing, and must zero
/// <c>plaintext</c> before returning so unverified plaintext is never released
/// (see <c>draft-irtf-cfrg-aegis-aead</c>, "Implementation Security").
/// </para>
/// </remarks>
internal interface IAhtolaAead : IDisposable
{
    /// <summary>Exact key length in bytes.</summary>
    int KeySize { get; }

    /// <summary>Exact nonce length in bytes.</summary>
    int NonceSize { get; }

    /// <summary>Exact authentication tag length in bytes. Always 16.</summary>
    int TagSize { get; }

    /// <summary>Encrypts <paramref name="plaintext"/> and writes the detached tag.</summary>
    void Encrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> associatedData);

    /// <summary>
    /// Authenticates and decrypts. Returns <see langword="false"/> (with
    /// <paramref name="plaintext"/> zeroed) when the tag does not verify.
    /// </summary>
    bool TryDecrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData);
}

/// <summary>
/// Wraps <see cref="AesGcm"/> so cipher IDs 1 and 2 keep producing exactly the
/// bytes they did before the AEGIS ciphers were added.
/// </summary>
/// <remarks>
/// A fresh <see cref="AesGcm"/> is constructed per operation. On Unix the BCL
/// type holds a mutable OpenSSL cipher context, so sharing one instance across
/// threads would be a data race; per-operation construction is what the codec
/// already did and is what keeps it concurrency-safe.
/// </remarks>
internal sealed class AhtolaAesGcmAead : IAhtolaAead
{
    private byte[]? _key;

    internal AhtolaAesGcmAead(ReadOnlySpan<byte> key)
    {
        KeySize = key.Length;
        _key = key.ToArray();
    }

    /// <inheritdoc />
    public int KeySize { get; }

    /// <inheritdoc />
    public int NonceSize => 12;

    /// <inheritdoc />
    public int TagSize => 16;

    /// <inheritdoc />
    public void Encrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        using var cipher = new AesGcm(Key, TagSize);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    /// <inheritdoc />
    public bool TryDecrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        try
        {
            using var cipher = new AesGcm(Key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return true;
        }
        catch (CryptographicException)
        {
            // AesGcm already zeroes the destination on failure; repeat it so the
            // contract holds regardless of the platform implementation.
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
    }

    private byte[] Key => _key ?? throw new ObjectDisposedException(nameof(AhtolaAesGcmAead));
}

/// <summary>
/// The pure-managed AEGIS AEAD used by Ahtola cipher IDs 3 through 8.
/// </summary>
/// <remarks>
/// Stateless apart from the key, so one instance is safe for concurrent use. The
/// AES round implementation is fixed at construction: production always uses the
/// accelerated policy (which itself falls back to the constant-time software
/// round), while the suite can pin the software round to prove the fallback is
/// byte-identical.
/// </remarks>
internal sealed class AhtolaAegisAead : IAhtolaAead
{
    private readonly AhtolaAegisAlgorithm _algorithm;
    private readonly int _degree;
    private readonly bool _forceSoftwareAesRound;
    private byte[]? _key;

    internal AhtolaAegisAead(
        AhtolaAegisAlgorithm algorithm,
        int degree,
        ReadOnlySpan<byte> key,
        bool forceSoftwareAesRound = false)
    {
        AegisParameters.ValidateDegree(degree);
        _algorithm = algorithm;
        _degree = degree;
        _forceSoftwareAesRound = forceSoftwareAesRound;

        KeySize = algorithm == AhtolaAegisAlgorithm.Aegis128 ? Aegis128X<AhtolaAcceleratedAesRound>.KeySize : Aegis256X<AhtolaAcceleratedAesRound>.KeySize;
        NonceSize = algorithm == AhtolaAegisAlgorithm.Aegis128 ? Aegis128X<AhtolaAcceleratedAesRound>.NonceSize : Aegis256X<AhtolaAcceleratedAesRound>.NonceSize;
        if (key.Length != KeySize)
        {
            throw new ArgumentException(
                $"AEGIS requires a {KeySize}-byte key, but the supplied key has {key.Length} bytes.",
                nameof(key));
        }

        _key = key.ToArray();
    }

    /// <inheritdoc />
    public int KeySize { get; }

    /// <inheritdoc />
    public int NonceSize { get; }

    /// <inheritdoc />
    public int TagSize => 16;

    /// <inheritdoc />
    public void Encrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        var key = Key;
        if (_algorithm == AhtolaAegisAlgorithm.Aegis128)
        {
            if (_forceSoftwareAesRound)
                Aegis128X<AhtolaSoftwareAesRound>.Encrypt(_degree, key, nonce, associatedData, plaintext, ciphertext, tag);
            else
                Aegis128X<AhtolaAcceleratedAesRound>.Encrypt(_degree, key, nonce, associatedData, plaintext, ciphertext, tag);
            return;
        }

        if (_forceSoftwareAesRound)
            Aegis256X<AhtolaSoftwareAesRound>.Encrypt(_degree, key, nonce, associatedData, plaintext, ciphertext, tag);
        else
            Aegis256X<AhtolaAcceleratedAesRound>.Encrypt(_degree, key, nonce, associatedData, plaintext, ciphertext, tag);
    }

    /// <inheritdoc />
    public bool TryDecrypt(
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        var key = Key;
        if (_algorithm == AhtolaAegisAlgorithm.Aegis128)
        {
            return _forceSoftwareAesRound
                ? Aegis128X<AhtolaSoftwareAesRound>.TryDecrypt(_degree, key, nonce, associatedData, ciphertext, tag, plaintext)
                : Aegis128X<AhtolaAcceleratedAesRound>.TryDecrypt(_degree, key, nonce, associatedData, ciphertext, tag, plaintext);
        }

        return _forceSoftwareAesRound
            ? Aegis256X<AhtolaSoftwareAesRound>.TryDecrypt(_degree, key, nonce, associatedData, ciphertext, tag, plaintext)
            : Aegis256X<AhtolaAcceleratedAesRound>.TryDecrypt(_degree, key, nonce, associatedData, ciphertext, tag, plaintext);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
    }

    private byte[] Key => _key ?? throw new ObjectDisposedException(nameof(AhtolaAegisAead));
}

/// <summary>Which AEGIS family an <see cref="AhtolaAegisAead"/> instantiates.</summary>
internal enum AhtolaAegisAlgorithm
{
    /// <summary>AEGIS-128X: 16-byte key and nonce, 32-byte-per-lane rate.</summary>
    Aegis128,

    /// <summary>AEGIS-256X: 32-byte key and nonce, 16-byte-per-lane rate.</summary>
    Aegis256,
}

/// <summary>Builds the AEAD primitive backing an Ahtola cipher id.</summary>
internal static class AhtolaAeadFactory
{
    /// <summary>
    /// Creates the AEAD for <paramref name="cipher"/>. The returned instance owns
    /// a copy of <paramref name="key"/> and zeroes it on <see cref="IDisposable.Dispose"/>.
    /// </summary>
    internal static IAhtolaAead Create(
        AhtolaEncryptionCipher cipher,
        ReadOnlySpan<byte> key,
        bool forceSoftwareAesRound = false)
        => cipher switch
        {
            AhtolaEncryptionCipher.Aes128Gcm or AhtolaEncryptionCipher.Aes256Gcm
                => new AhtolaAesGcmAead(key),
            AhtolaEncryptionCipher.Aegis256
                => new AhtolaAegisAead(AhtolaAegisAlgorithm.Aegis256, 1, key, forceSoftwareAesRound),
            AhtolaEncryptionCipher.Aegis256X2
                => new AhtolaAegisAead(AhtolaAegisAlgorithm.Aegis256, 2, key, forceSoftwareAesRound),
            AhtolaEncryptionCipher.Aegis256X4
                => new AhtolaAegisAead(AhtolaAegisAlgorithm.Aegis256, 4, key, forceSoftwareAesRound),
            AhtolaEncryptionCipher.Aegis128L
                => new AhtolaAegisAead(AhtolaAegisAlgorithm.Aegis128, 1, key, forceSoftwareAesRound),
            AhtolaEncryptionCipher.Aegis128X2
                => new AhtolaAegisAead(AhtolaAegisAlgorithm.Aegis128, 2, key, forceSoftwareAesRound),
            AhtolaEncryptionCipher.Aegis128X4
                => new AhtolaAegisAead(AhtolaAegisAlgorithm.Aegis128, 4, key, forceSoftwareAesRound),
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                AhtolaEncryptedPageFormat.SupportedCipherSummary),
        };
}
