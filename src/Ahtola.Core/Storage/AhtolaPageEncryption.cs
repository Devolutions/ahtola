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
    internal const int MetadataSize = TagSize + NonceSize;
    internal const int TagSize = 16;
    internal const int NonceSize = 12;
    internal const byte FormatVersion = 0;
    private const int SqliteHeaderSize = 100;
    private const int AhtolaHeaderSize = 16;

    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;
    // Fixed 5-byte magic so version/cipher remain at offsets 5/6 inside the 16-byte header.
    private static ReadOnlySpan<byte> AhtolaHeaderPrefix => "AHTLA"u8;

    private readonly byte[] _key;
    private bool _disposed;

    public AhtolaPageEncryption(AhtolaEncryptionCipher cipher, ReadOnlySpan<byte> key, int pageSize)
    {
        Cipher = cipher;
        PageSize = pageSize;
        if (pageSize <= SqliteHeaderSize + MetadataSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "The page is too small for Ahtola encryption metadata.");
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
        if (header.Length < AhtolaHeaderSize)
            throw new InvalidDataException("Encrypted Ahtola database header is truncated.");
        if (!header[..AhtolaHeaderPrefix.Length].SequenceEqual(AhtolaHeaderPrefix))
            throw new InvalidDataException("Database does not contain a Ahtola encrypted header.");
        if (header[5] != FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Ahtola encrypted database format version {header[5]}; "
                + "managed storage supports only format version 0 and will not infer or fall back to another format.");
        }
        if (header[6] is not (byte)AhtolaEncryptionCipher.Aes128Gcm and not (byte)AhtolaEncryptionCipher.Aes256Gcm)
        {
            throw new InvalidDataException(
                $"Encrypted database uses Ahtola cipher ID {header[6]} ({GetCipherName(header[6])}); "
                + "managed storage supports only cipher ID 1 (AES-128-GCM) and cipher ID 2 (AES-256-GCM) "
                + "for format version 0 and will not infer or fall back to another cipher.");
        }
        if (header[6] != (byte)Cipher)
        {
            throw new InvalidDataException(
                $"Encrypted database uses Ahtola cipher ID {header[6]} ({GetCipherName(header[6])}), "
                + $"but the supplied options specify cipher ID {(byte)Cipher} ({GetCipherName((byte)Cipher)}); "
                + "cipher fallback is not permitted.");
        }
        if (header[7..AhtolaHeaderSize].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Ahtola encrypted database header has non-zero reserved bytes.");
    }

    public byte[] EncryptPage(ReadOnlySpan<byte> page, uint pageNumber)
    {
        ThrowIfDisposed();
        ValidatePage(page, pageNumber);
        if (page[^MetadataSize..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                $"Plaintext page {pageNumber} uses the {MetadataSize} SQLite reserved bytes required for Ahtola encryption metadata.");
        }
        if (pageNumber == 1)
            return EncryptFirstPage(page);

        var encrypted = new byte[PageSize];
        var payloadLength = PageSize - MetadataSize;
        Encrypt(
            page[..payloadLength],
            encrypted.AsSpan(..payloadLength),
            encrypted.AsSpan(payloadLength, TagSize),
            encrypted.AsSpan(PageSize - NonceSize, NonceSize),
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
        var payloadLength = PageSize - MetadataSize;
        Decrypt(
            encryptedPage[..payloadLength],
            encryptedPage.Slice(payloadLength, TagSize),
            encryptedPage[^NonceSize..],
            plaintext.AsSpan(0, payloadLength),
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
        if (!page[..SqliteHeader.Length].SequenceEqual(SqliteHeader))
            throw new InvalidDataException("The first plaintext page must contain an SQLite format 3 header.");

        var encrypted = new byte[PageSize];
        AhtolaHeaderPrefix.CopyTo(encrypted);
        encrypted[5] = FormatVersion;
        encrypted[6] = (byte)Cipher;
        page[AhtolaHeaderSize..SqliteHeaderSize].CopyTo(encrypted.AsSpan(AhtolaHeaderSize));

        var payloadLength = PageSize - SqliteHeaderSize - MetadataSize;
        Encrypt(
            page.Slice(SqliteHeaderSize, payloadLength),
            encrypted.AsSpan(SqliteHeaderSize, payloadLength),
            encrypted.AsSpan(PageSize - MetadataSize, TagSize),
            encrypted.AsSpan(PageSize - NonceSize, NonceSize),
            encrypted.AsSpan(0, SqliteHeaderSize));
        return encrypted;
    }

    private byte[] DecryptFirstPage(ReadOnlySpan<byte> encryptedPage)
    {
        ValidateEncryptedHeader(encryptedPage);

        var plaintext = new byte[PageSize];
        SqliteHeader.CopyTo(plaintext);
        encryptedPage[AhtolaHeaderSize..SqliteHeaderSize].CopyTo(plaintext.AsSpan(AhtolaHeaderSize));
        var payloadLength = PageSize - SqliteHeaderSize - MetadataSize;
        Decrypt(
            encryptedPage.Slice(SqliteHeaderSize, payloadLength),
            encryptedPage.Slice(PageSize - MetadataSize, TagSize),
            encryptedPage[^NonceSize..],
            plaintext.AsSpan(SqliteHeaderSize, payloadLength),
            encryptedPage[..SqliteHeaderSize],
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
            throw new InvalidDataException(
                $"Encrypted Ahtola page {pageNumber} failed authentication. The encryption key is incorrect or the file was tampered with.",
                exception);
        }
    }

    private void ValidatePage(ReadOnlySpan<byte> page, uint pageNumber)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        if (page.Length != PageSize)
            throw new ArgumentException($"Encrypted page data must be exactly {PageSize} bytes.", nameof(page));
    }

    private static string GetCipherName(byte cipherId)
        => cipherId switch
        {
            0 => "none",
            1 => "AES-128-GCM",
            2 => "AES-256-GCM",
            3 => "AEGIS-256",
            4 => "AEGIS-256X2",
            5 => "AEGIS-256X4",
            6 => "AEGIS-128L",
            7 => "AEGIS-128X2",
            8 => "AEGIS-128X4",
            _ => "unknown",
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
