using System.Security.Cryptography;

namespace Ahtola.Core.Storage;

/// <summary>
/// Cipher identifiers 1 and 2 from version 0 of Ahtola's encrypted page format.
/// Other Ahtola cipher identifiers are intentionally rejected by managed storage.
/// </summary>
public enum AhtolaEncryptionCipher : byte
{
    Aes128Gcm = 1,
    Aes256Gcm = 2,
}

/// <summary>
/// Supplies an AES-GCM key for a Ahtola encrypted SQLite database. The managed
/// storage engine supports only the AES-GCM cipher variants because their page
/// encoding exactly matches the Rust engine and they are provided by .NET.
/// </summary>
public sealed class AhtolaEncryptionOptions : IDisposable
{
    private byte[]? _key;

    /// <summary>Initializes encryption options from an exact AES key.</summary>
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

    internal AesGcm CreateAesGcm()
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(AhtolaEncryptionOptions));
        return new AesGcm(key, AhtolaEncryptedPageFormat.TagSize);
    }

    internal static int GetRequiredKeyLength(AhtolaEncryptionCipher cipher)
        => cipher switch
        {
            AhtolaEncryptionCipher.Aes128Gcm => 16,
            AhtolaEncryptionCipher.Aes256Gcm => 32,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                "The managed encrypted store supports only Ahtola AES-GCM cipher IDs 1 and 2."),
        };

    private static AhtolaEncryptionCipher ConvertCipher(Enum cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        return cipher.ToString() switch
        {
            nameof(AhtolaEncryptionCipher.Aes128Gcm) => AhtolaEncryptionCipher.Aes128Gcm,
            nameof(AhtolaEncryptionCipher.Aes256Gcm) => AhtolaEncryptionCipher.Aes256Gcm,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                "The managed encrypted store supports only Ahtola AES-GCM cipher IDs 1 and 2."),
        };
    }
}

internal sealed class AhtolaPageEncryption : IDisposable
{
    internal const int MetadataSize = AhtolaEncryptedPageFormat.MetadataSize;
    internal const int TagSize = AhtolaEncryptedPageFormat.TagSize;
    internal const int NonceSize = AhtolaEncryptedPageFormat.NonceSize;
    internal const byte FormatVersion = AhtolaEncryptedPageFormat.FormatVersion;
    private const int SqliteHeaderSize = AhtolaEncryptedPageFormat.SqliteHeaderSize;

    private readonly byte[] _key;
    private bool _disposed;

    public AhtolaPageEncryption(AhtolaEncryptionCipher cipher, ReadOnlySpan<byte> key, int pageSize)
    {
        Cipher = cipher;
        PageSize = pageSize;
        AhtolaEncryptedPageFormat.ValidatePageSize(pageSize);
        if (key.Length != AhtolaEncryptionOptions.GetRequiredKeyLength(cipher))
            throw new ArgumentException("The encryption key length does not match the configured cipher.", nameof(key));

        _key = key.ToArray();
    }

    public AhtolaEncryptionCipher Cipher { get; }

    public int PageSize { get; }

    public SqliteDatabaseHeader PrepareHeader(SqliteDatabaseHeader header)
    {
        ThrowIfDisposed();
        if (header.PageSize != PageSize)
            throw new InvalidOperationException("The encryption context and database header page sizes must match.");
        if (header.PageSize - MetadataSize < SqliteDatabaseHeader.MinimumUsableSpace)
            throw new InvalidOperationException("Encryption metadata leaves too little usable SQLite page space.");

        return header with { ReservedSpace = MetadataSize };
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
        AhtolaEncryptedPageFormat.ValidatePlaintextReservedBytes(page, pageNumber);
        if (pageNumber == 1)
            return EncryptFirstPage(page);

        var encrypted = new byte[PageSize];
        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber);
        Encrypt(
            page.Slice(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.TagOffset, TagSize),
            encrypted.AsSpan(regions.NonceOffset, NonceSize),
            []);
        return encrypted;
    }

    public byte[] DecryptPage(ReadOnlySpan<byte> encryptedPage, uint pageNumber)
    {
        ThrowIfDisposed();
        ValidatePage(encryptedPage, pageNumber);
        if (pageNumber == 1)
            return DecryptFirstPage(encryptedPage);

        var plaintext = new byte[PageSize];
        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber);
        Decrypt(
            encryptedPage.Slice(regions.PayloadOffset, regions.PayloadLength),
            encryptedPage.Slice(regions.TagOffset, TagSize),
            encryptedPage.Slice(regions.NonceOffset, NonceSize),
            plaintext.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            [],
            pageNumber);
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    private byte[] EncryptFirstPage(ReadOnlySpan<byte> page)
    {
        var encrypted = new byte[PageSize];
        AhtolaEncryptedPageFormat.WriteEncryptedHeaderPrefix(encrypted, page, Cipher);

        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber: 1);
        Encrypt(
            page.Slice(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.TagOffset, TagSize),
            encrypted.AsSpan(regions.NonceOffset, NonceSize),
            encrypted.AsSpan(regions.AssociatedDataOffset, regions.AssociatedDataLength));
        return encrypted;
    }

    private byte[] DecryptFirstPage(ReadOnlySpan<byte> encryptedPage)
    {
        ValidateEncryptedHeader(encryptedPage);

        var plaintext = new byte[PageSize];
        AhtolaEncryptedPageFormat.RestorePlaintextHeaderPrefix(plaintext, encryptedPage);
        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber: 1);
        Decrypt(
            encryptedPage.Slice(regions.PayloadOffset, regions.PayloadLength),
            encryptedPage.Slice(regions.TagOffset, TagSize),
            encryptedPage.Slice(regions.NonceOffset, NonceSize),
            plaintext.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            encryptedPage.Slice(regions.AssociatedDataOffset, regions.AssociatedDataLength),
            pageNumber: 1);
        return plaintext;
    }

    private void Encrypt(
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        Span<byte> nonce,
        ReadOnlySpan<byte> associatedData)
    {
        RandomNumberGenerator.Fill(nonce);
        using var cipher = new AesGcm(_key, TagSize);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    private void Decrypt(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> nonce,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        uint pageNumber)
    {
        try
        {
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (CryptographicException exception)
        {
            throw AhtolaEncryptedPageFormat.CreateAuthenticationFailure(pageNumber, exception);
        }
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
