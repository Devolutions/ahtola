namespace Ahtola.Core.Storage;

/// <summary>
/// The regions of one AHTLA version 0 encrypted page, expressed as offsets into
/// a full page image.
/// </summary>
/// <remarks>
/// <see cref="AssociatedDataLength"/> is zero for every page except page 1,
/// whose 100-byte visible header authenticates the encrypted payload.
/// </remarks>
internal readonly record struct AhtolaEncryptedPageRegions(
    int PayloadOffset,
    int PayloadLength,
    int TagOffset,
    int NonceOffset,
    int AssociatedDataOffset,
    int AssociatedDataLength);

/// <summary>
/// The byte layout of Ahtola's AES-GCM encrypted page format, factored out of
/// <see cref="AhtolaPageEncryption"/> so callers that cannot use the synchronous
/// <see cref="System.Security.Cryptography.AesGcm"/> primitive (notably the
/// browser package, whose Web Crypto bindings are asynchronous) still produce
/// byte-identical pages.
/// </summary>
/// <remarks>
/// Every offset here is part of the on-disk contract shared with Turso's Rust
/// engine. Changing any constant changes the file format.
/// </remarks>
internal static class AhtolaEncryptedPageFormat
{
    /// <summary>Reserved bytes consumed per page: a 16-byte tag plus a 12-byte nonce.</summary>
    internal const int MetadataSize = TagSize + NonceSize;

    /// <summary>AES-GCM authentication tag length.</summary>
    internal const int TagSize = 16;

    /// <summary>AES-GCM nonce length.</summary>
    internal const int NonceSize = 12;

    /// <summary>The only encrypted-page format version managed storage accepts.</summary>
    internal const byte FormatVersion = 0;

    /// <summary>The SQLite database header length, which stays visible on page 1.</summary>
    internal const int SqliteHeaderSize = 100;

    /// <summary>The AHTLA header length that replaces the SQLite magic on page 1.</summary>
    internal const int AhtolaHeaderSize = 16;

    /// <summary>The plaintext SQLite file magic.</summary>
    internal static ReadOnlySpan<byte> SqliteHeaderMagic => "SQLite format 3\0"u8;

    /// <summary>
    /// Fixed 5-byte magic so version/cipher remain at offsets 5/6 inside the
    /// 16-byte AHTLA header.
    /// </summary>
    internal static ReadOnlySpan<byte> AhtolaHeaderMagic => "AHTLA"u8;

    /// <summary>Whether <paramref name="header"/> starts with the AHTLA magic.</summary>
    internal static bool IsAhtolaEncrypted(ReadOnlySpan<byte> header)
        => header.Length >= AhtolaHeaderMagic.Length
           && header[..AhtolaHeaderMagic.Length].SequenceEqual(AhtolaHeaderMagic);

    /// <summary>Rejects page sizes that cannot hold the header plus encryption metadata.</summary>
    internal static void ValidatePageSize(int pageSize)
    {
        if (pageSize <= SqliteHeaderSize + MetadataSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                "The page is too small for Ahtola encryption metadata.");
        }
    }

    /// <summary>Reports the encrypted regions of <paramref name="pageNumber"/>.</summary>
    internal static AhtolaEncryptedPageRegions Describe(int pageSize, uint pageNumber)
    {
        ValidatePageSize(pageSize);
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");

        var payloadOffset = pageNumber == 1 ? SqliteHeaderSize : 0;
        return new AhtolaEncryptedPageRegions(
            payloadOffset,
            pageSize - payloadOffset - MetadataSize,
            pageSize - MetadataSize,
            pageSize - NonceSize,
            AssociatedDataOffset: 0,
            AssociatedDataLength: pageNumber == 1 ? SqliteHeaderSize : 0);
    }

    /// <summary>
    /// Writes the visible prefix of an encrypted page 1: the AHTLA magic, format
    /// version, cipher id, and the SQLite header bytes that stay in the clear.
    /// </summary>
    internal static void WriteEncryptedHeaderPrefix(
        Span<byte> encryptedPage,
        ReadOnlySpan<byte> plaintextPage,
        AhtolaEncryptionCipher cipher)
    {
        if (!plaintextPage[..SqliteHeaderMagic.Length].SequenceEqual(SqliteHeaderMagic))
            throw new InvalidDataException("The first plaintext page must contain an SQLite format 3 header.");

        AhtolaHeaderMagic.CopyTo(encryptedPage);
        encryptedPage[5] = FormatVersion;
        encryptedPage[6] = (byte)cipher;
        encryptedPage[7..AhtolaHeaderSize].Clear();
        plaintextPage[AhtolaHeaderSize..SqliteHeaderSize].CopyTo(encryptedPage[AhtolaHeaderSize..]);
    }

    /// <summary>Restores the plaintext SQLite header prefix of a decrypted page 1.</summary>
    internal static void RestorePlaintextHeaderPrefix(
        Span<byte> plaintextPage,
        ReadOnlySpan<byte> encryptedPage)
    {
        SqliteHeaderMagic.CopyTo(plaintextPage);
        encryptedPage[AhtolaHeaderSize..SqliteHeaderSize].CopyTo(plaintextPage[AhtolaHeaderSize..]);
    }

    /// <summary>
    /// Validates an encrypted page 1 header against <paramref name="expectedCipher"/>,
    /// refusing any format or cipher fallback.
    /// </summary>
    internal static void ValidateEncryptedHeader(
        ReadOnlySpan<byte> header,
        AhtolaEncryptionCipher expectedCipher)
    {
        if (header.Length < AhtolaHeaderSize)
            throw new InvalidDataException("Encrypted Ahtola database header is truncated.");
        if (!IsAhtolaEncrypted(header))
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
        if (header[6] != (byte)expectedCipher)
        {
            throw new InvalidDataException(
                $"Encrypted database uses Ahtola cipher ID {header[6]} ({GetCipherName(header[6])}), "
                + $"but the supplied options specify cipher ID {(byte)expectedCipher} ({GetCipherName((byte)expectedCipher)}); "
                + "cipher fallback is not permitted.");
        }
        if (header[7..AhtolaHeaderSize].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Ahtola encrypted database header has non-zero reserved bytes.");
    }

    /// <summary>
    /// Rejects plaintext pages that already use the reserved bytes the encrypted
    /// layout needs for its tag and nonce.
    /// </summary>
    internal static void ValidatePlaintextReservedBytes(ReadOnlySpan<byte> page, uint pageNumber)
    {
        if (page[^MetadataSize..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                $"Plaintext page {pageNumber} uses the {MetadataSize} SQLite reserved bytes required for Ahtola encryption metadata.");
        }
    }

    /// <summary>The failure raised when a page does not authenticate.</summary>
    internal static InvalidDataException CreateAuthenticationFailure(uint pageNumber, Exception? inner)
        => new(
            $"Encrypted Ahtola page {pageNumber} failed authentication. The encryption key is incorrect or the file was tampered with.",
            inner);

    /// <summary>Maps an Ahtola cipher id to its documented name.</summary>
    internal static string GetCipherName(byte cipherId)
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
}
