using System.Security.Cryptography;
using Ahtola.Core.Storage.Crypto;

namespace Ahtola.Core.Storage;

/// <summary>
/// The cipher identifiers defined by version 0 of Ahtola's encrypted page
/// format. The numeric values are the on-disk cipher ids shared with Turso's
/// Rust engine (<c>core/storage/encryption.rs</c>), so they must never be
/// renumbered.
/// </summary>
public enum AhtolaEncryptionCipher : byte
{
    /// <summary>AES-128-GCM: 16-byte key, 12-byte nonce, 28 reserved bytes.</summary>
    Aes128Gcm = 1,

    /// <summary>AES-256-GCM: 32-byte key, 12-byte nonce, 28 reserved bytes.</summary>
    Aes256Gcm = 2,

    /// <summary>AEGIS-256: 32-byte key, 32-byte nonce, 48 reserved bytes.</summary>
    Aegis256 = 3,

    /// <summary>AEGIS-256X2: 32-byte key, 32-byte nonce, 48 reserved bytes.</summary>
    Aegis256X2 = 4,

    /// <summary>AEGIS-256X4: 32-byte key, 32-byte nonce, 48 reserved bytes.</summary>
    Aegis256X4 = 5,

    /// <summary>AEGIS-128L: 16-byte key, 16-byte nonce, 32 reserved bytes.</summary>
    Aegis128L = 6,

    /// <summary>AEGIS-128X2: 16-byte key, 16-byte nonce, 32 reserved bytes.</summary>
    Aegis128X2 = 7,

    /// <summary>AEGIS-128X4: 16-byte key, 16-byte nonce, 32 reserved bytes.</summary>
    Aegis128X4 = 8,
}

/// <summary>
/// Supplies the page-encryption key for a Ahtola encrypted SQLite database.
/// AES-GCM is provided by .NET; the AEGIS variants are implemented in managed
/// code so their page bytes match the Rust engine exactly on every target,
/// including browser-wasm.
/// </summary>
public sealed class AhtolaEncryptionOptions : IDisposable
{
    private byte[]? _key;

    /// <summary>Initializes encryption options from an exact key.</summary>
    public AhtolaEncryptionOptions(AhtolaEncryptionCipher cipher, ReadOnlySpan<byte> key)
    {
        Cipher = cipher;
        var requiredKeyLength = GetRequiredKeyLength(cipher);
        if (key.Length != requiredKeyLength)
        {
            throw new ArgumentException(
                $"{cipher} requires a {requiredKeyLength}-byte key, but the supplied key has {key.Length} bytes.",
                nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>
    /// Initializes encryption options from another enumeration whose member name
    /// matches one of <see cref="AhtolaEncryptionCipher"/>'s, ignoring case.
    /// </summary>
    public AhtolaEncryptionOptions(Enum cipher, ReadOnlySpan<byte> key)
        : this(ConvertCipher(cipher), key)
    {
    }

    /// <summary>The page cipher that will be stored in the Ahtola encrypted header.</summary>
    public AhtolaEncryptionCipher Cipher { get; }

    /// <summary>Creates encryption options from Ahtola's hex-encoded key representation.</summary>
    public static AhtolaEncryptionOptions FromHex(AhtolaEncryptionCipher cipher, string hexKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hexKey);

        try
        {
            var key = Convert.FromHexString(hexKey.Trim());
            try
            {
                return new AhtolaEncryptionOptions(cipher, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Encryption keys must be hexadecimal.", nameof(hexKey), exception);
        }
    }

    /// <summary>
    /// Creates encryption options from a hex key and a cipher enumeration whose
    /// member name matches one of <see cref="AhtolaEncryptionCipher"/>'s.
    /// </summary>
    public static AhtolaEncryptionOptions FromHex<TCipher>(TCipher cipher, string hexKey)
        where TCipher : struct, Enum
    {
        return FromHex(ConvertCipher(cipher), hexKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_key is null)
            return;

        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    internal AhtolaPageEncryption CreatePageEncryption(int pageSize)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(AhtolaEncryptionOptions));
        return new AhtolaPageEncryption(Cipher, key, pageSize);
    }

    internal AhtolaEncryptionOptions CreateOwnedCopy()
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(AhtolaEncryptionOptions));
        return new AhtolaEncryptionOptions(Cipher, key);
    }

    /// <summary>
    /// Builds the AEAD primitive for this configuration. Used by callers that
    /// frame pages themselves (the browser package and the suite) instead of
    /// going through <see cref="AhtolaPageEncryption"/>.
    /// </summary>
    internal IAhtolaAead CreateAead(bool forceSoftwareAesRound = false)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(AhtolaEncryptionOptions));
        return AhtolaAeadFactory.Create(Cipher, key, forceSoftwareAesRound);
    }

    internal static int GetRequiredKeyLength(AhtolaEncryptionCipher cipher)
        => AhtolaEncryptedPageFormat.GetParameters(cipher).KeySize;

    /// <summary>
    /// Resolves a foreign cipher enumeration by member name. The Ahtola data layer
    /// spells its members <c>Aegis128l</c>/<c>Aegis256x2</c> while the storage
    /// layer spells them <c>Aegis128L</c>/<c>Aegis256X2</c>, so the comparison is
    /// deliberately case-insensitive. Name lookup only happens on the cold options
    /// path, never per page.
    /// </summary>
    private static AhtolaEncryptionCipher ConvertCipher(Enum cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        var name = cipher.ToString();
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aes128Gcm)))
            return AhtolaEncryptionCipher.Aes128Gcm;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aes256Gcm)))
            return AhtolaEncryptionCipher.Aes256Gcm;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aegis256)))
            return AhtolaEncryptionCipher.Aegis256;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aegis256X2)))
            return AhtolaEncryptionCipher.Aegis256X2;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aegis256X4)))
            return AhtolaEncryptionCipher.Aegis256X4;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aegis128L)))
            return AhtolaEncryptionCipher.Aegis128L;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aegis128X2)))
            return AhtolaEncryptionCipher.Aegis128X2;
        if (Matches(name, nameof(AhtolaEncryptionCipher.Aegis128X4)))
            return AhtolaEncryptionCipher.Aegis128X4;

        throw new ArgumentOutOfRangeException(
            nameof(cipher),
            cipher,
            AhtolaEncryptedPageFormat.SupportedCipherSummary);

        static bool Matches(string value, string expected)
            => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Frames whole SQLite pages in Ahtola's AHTLA version 0 encrypted format.
/// </summary>
/// <remarks>
/// Safe for concurrent use: the AEAD primitive keeps no mutable per-operation
/// state, and every scratch buffer is local to the call.
/// </remarks>
internal sealed class AhtolaPageEncryption : IDisposable
{
    internal const byte FormatVersion = AhtolaEncryptedPageFormat.FormatVersion;
    internal const int TagSize = AhtolaEncryptedPageFormat.TagSize;

    private readonly IAhtolaAead _aead;
    private readonly AhtolaCipherParameters _parameters;
    private bool _disposed;

    public AhtolaPageEncryption(AhtolaEncryptionCipher cipher, ReadOnlySpan<byte> key, int pageSize)
        : this(cipher, key, pageSize, forceSoftwareAesRound: false)
    {
    }

    internal AhtolaPageEncryption(
        AhtolaEncryptionCipher cipher,
        ReadOnlySpan<byte> key,
        int pageSize,
        bool forceSoftwareAesRound)
    {
        Cipher = cipher;
        PageSize = pageSize;
        _parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        AhtolaEncryptedPageFormat.ValidatePageSize(pageSize, _parameters);
        if (key.Length != _parameters.KeySize)
            throw new ArgumentException("The encryption key length does not match the configured cipher.", nameof(key));

        _aead = AhtolaAeadFactory.Create(cipher, key, forceSoftwareAesRound);
    }

    public AhtolaEncryptionCipher Cipher { get; }

    public int PageSize { get; }

    /// <summary>Reserved bytes this cipher consumes in every page.</summary>
    public int MetadataSize => _parameters.MetadataSize;

    /// <summary>Nonce bytes stored at the tail of every page.</summary>
    public int NonceSize => _parameters.NonceSize;

    public SqliteDatabaseHeader PrepareHeader(SqliteDatabaseHeader header)
    {
        ThrowIfDisposed();
        if (header.PageSize != PageSize)
            throw new InvalidOperationException("The encryption context and database header page sizes must match.");
        if (header.PageSize - MetadataSize < SqliteDatabaseHeader.MinimumUsableSpace)
            throw new InvalidOperationException("Encryption metadata leaves too little usable SQLite page space.");

        return header with { ReservedSpace = checked((byte)MetadataSize) };
    }

    public void ValidateEncryptedHeader(ReadOnlySpan<byte> header)
    {
        ThrowIfDisposed();
        AhtolaEncryptedPageFormat.ValidateEncryptedHeader(header, Cipher);
    }

    public byte[] EncryptPage(ReadOnlySpan<byte> page, uint pageNumber)
    {
        ThrowIfDisposed();
        ValidatePage(page, pageNumber);
        AhtolaEncryptedPageFormat.ValidatePlaintextReservedBytes(page, pageNumber, _parameters);

        var encrypted = new byte[PageSize];
        var associatedDataLength = 0;
        if (pageNumber == 1)
        {
            AhtolaEncryptedPageFormat.WriteEncryptedHeaderPrefix(encrypted, page, Cipher);
            associatedDataLength = AhtolaEncryptedPageFormat.SqliteHeaderSize;
        }

        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber, _parameters);
        Encrypt(
            page.Slice(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.TagOffset, regions.TagLength),
            encrypted.AsSpan(regions.NonceOffset, regions.NonceLength),
            encrypted.AsSpan(regions.AssociatedDataOffset, associatedDataLength));
        return encrypted;
    }

    public byte[] DecryptPage(ReadOnlySpan<byte> encryptedPage, uint pageNumber)
    {
        ThrowIfDisposed();
        ValidatePage(encryptedPage, pageNumber);

        var plaintext = new byte[PageSize];
        if (pageNumber == 1)
        {
            ValidateEncryptedHeader(encryptedPage);
            AhtolaEncryptedPageFormat.RestorePlaintextHeaderPrefix(plaintext, encryptedPage);
        }

        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber, _parameters);
        Decrypt(
            encryptedPage.Slice(regions.PayloadOffset, regions.PayloadLength),
            encryptedPage.Slice(regions.TagOffset, regions.TagLength),
            encryptedPage.Slice(regions.NonceOffset, regions.NonceLength),
            plaintext.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            encryptedPage.Slice(regions.AssociatedDataOffset, regions.AssociatedDataLength),
            pageNumber);
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _aead.Dispose();
    }

    private void Encrypt(
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        Span<byte> nonce,
        ReadOnlySpan<byte> associatedData)
    {
        // A fresh random nonce per write, exactly like Turso's generate_secure_nonce.
        // Deriving nonces from page numbers would be catastrophic for AEGIS.
        RandomNumberGenerator.Fill(nonce);
        _aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    private void Decrypt(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> nonce,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        uint pageNumber)
    {
        if (!_aead.TryDecrypt(nonce, ciphertext, tag, plaintext, associatedData))
            throw AhtolaEncryptedPageFormat.CreateAuthenticationFailure(pageNumber, inner: null);
    }

    private void ValidatePage(ReadOnlySpan<byte> page, uint pageNumber)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        if (page.Length != PageSize)
            throw new ArgumentException($"Encrypted page data must be exactly {PageSize} bytes.", nameof(page));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
