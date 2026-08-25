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
    int TagLength,
    int NonceOffset,
    int NonceLength,
    int AssociatedDataOffset,
    int AssociatedDataLength);

/// <summary>
/// The per-cipher sizes that shape an AHTLA version 0 encrypted page. Mirrors
/// Turso's <c>CipherMode::{required_key_size, nonce_size, tag_size, cipher_id}</c>
/// in <c>core/storage/encryption.rs</c>.
/// </summary>
internal readonly record struct AhtolaCipherParameters(byte CipherId, int KeySize, int NonceSize)
{
    /// <summary>Authentication tag length. Every Ahtola cipher uses a 128-bit tag.</summary>
    internal int TagSize => AhtolaEncryptedPageFormat.TagSize;

    /// <summary>Reserved bytes consumed per page: the tag followed by the nonce.</summary>
    internal int MetadataSize => NonceSize + TagSize;
}

/// <summary>
/// The byte layout of Ahtola's encrypted page format, factored out of
/// <see cref="AhtolaPageEncryption"/> so callers that cannot use a synchronous
/// AEAD primitive (notably the browser package, whose Web Crypto bindings are
/// asynchronous) still produce byte-identical pages.
/// </summary>
/// <remarks>
/// Every offset here is part of the on-disk contract shared with Turso's Rust
/// engine. Changing any constant changes the file format. The frame is always
/// <c>ciphertext || tag || nonce</c>; only the nonce length varies by cipher.
/// </remarks>
internal static class AhtolaEncryptedPageFormat
{
    /// <summary>Authentication tag length, identical for every supported cipher.</summary>
    internal const int TagSize = 16;

    /// <summary>The only encrypted-page format version managed storage accepts.</summary>
    internal const byte FormatVersion = 0;

    /// <summary>The SQLite database header length, which stays visible on page 1.</summary>
    internal const int SqliteHeaderSize = 100;

    /// <summary>The AHTLA header length that replaces the SQLite magic on page 1.</summary>
    internal const int AhtolaHeaderSize = 16;

    /// <summary>Offset of the SQLite reserved-space byte inside the visible page-1 header.</summary>
    internal const int ReservedSpaceOffset = 20;

    /// <summary>The highest cipher id defined by format version 0.</summary>
    internal const byte HighestCipherId = 8;

    /// <summary>Shared wording for "this cipher id is not part of format version 0".</summary>
    internal const string SupportedCipherSummary =
        "The managed encrypted store supports only Ahtola format version 0 cipher IDs 1 through 8 "
        + "(AES-128-GCM, AES-256-GCM, AEGIS-256, AEGIS-256X2, AEGIS-256X4, AEGIS-128L, AEGIS-128X2, AEGIS-128X4).";

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

    /// <summary>
    /// Reports the key, nonce and metadata sizes for <paramref name="cipher"/>.
    /// The numeric enum values are the Turso cipher ids, so the header byte and
    /// this table can never drift apart.
    /// </summary>
    internal static AhtolaCipherParameters GetParameters(AhtolaEncryptionCipher cipher)
        => cipher switch
        {
            AhtolaEncryptionCipher.Aes128Gcm => new AhtolaCipherParameters(1, KeySize: 16, NonceSize: 12),
            AhtolaEncryptionCipher.Aes256Gcm => new AhtolaCipherParameters(2, KeySize: 32, NonceSize: 12),
            AhtolaEncryptionCipher.Aegis256 => new AhtolaCipherParameters(3, KeySize: 32, NonceSize: 32),
            AhtolaEncryptionCipher.Aegis256X2 => new AhtolaCipherParameters(4, KeySize: 32, NonceSize: 32),
            AhtolaEncryptionCipher.Aegis256X4 => new AhtolaCipherParameters(5, KeySize: 32, NonceSize: 32),
            AhtolaEncryptionCipher.Aegis128L => new AhtolaCipherParameters(6, KeySize: 16, NonceSize: 16),
            AhtolaEncryptionCipher.Aegis128X2 => new AhtolaCipherParameters(7, KeySize: 16, NonceSize: 16),
            AhtolaEncryptionCipher.Aegis128X4 => new AhtolaCipherParameters(8, KeySize: 16, NonceSize: 16),
            _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher, SupportedCipherSummary),
        };

    /// <summary>Whether <paramref name="cipherId"/> is defined by format version 0.</summary>
    internal static bool IsSupportedCipherId(byte cipherId)
        => cipherId is >= 1 and <= HighestCipherId;

    /// <summary>Maps a format version 0 cipher id back to its enum member.</summary>
    internal static AhtolaEncryptionCipher FromCipherId(byte cipherId)
    {
        if (!IsSupportedCipherId(cipherId))
            throw new ArgumentOutOfRangeException(nameof(cipherId), cipherId, SupportedCipherSummary);

        return (AhtolaEncryptionCipher)cipherId;
    }

    /// <summary>Rejects page sizes that cannot hold the header plus encryption metadata.</summary>
    internal static void ValidatePageSize(int pageSize, in AhtolaCipherParameters parameters)
    {
        if (pageSize <= SqliteHeaderSize + parameters.MetadataSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"The page is too small for {GetCipherName(parameters.CipherId)} encryption metadata "
                + $"({parameters.MetadataSize} reserved bytes).");
        }
    }

    /// <summary>Reports the encrypted regions of <paramref name="pageNumber"/>.</summary>
    internal static AhtolaEncryptedPageRegions Describe(
        int pageSize,
        uint pageNumber,
        in AhtolaCipherParameters parameters)
    {
        ValidatePageSize(pageSize, parameters);
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");

        var payloadOffset = pageNumber == 1 ? SqliteHeaderSize : 0;
        var metadataSize = parameters.MetadataSize;
        return new AhtolaEncryptedPageRegions(
            payloadOffset,
            pageSize - payloadOffset - metadataSize,
            TagOffset: pageSize - metadataSize,
            TagLength: parameters.TagSize,
            NonceOffset: pageSize - parameters.NonceSize,
            NonceLength: parameters.NonceSize,
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
        encryptedPage[6] = GetParameters(cipher).CipherId;
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
        var expected = GetParameters(expectedCipher);
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
        if (!IsSupportedCipherId(header[6]))
        {
            throw new InvalidDataException(
                $"Encrypted database uses Ahtola cipher ID {header[6]} ({GetCipherName(header[6])}); "
                + $"managed storage supports only cipher IDs 1 through {HighestCipherId} for format version 0 "
                + "and will not infer or fall back to another cipher.");
        }
        if (header[6] != expected.CipherId)
        {
            throw new InvalidDataException(
                $"Encrypted database uses Ahtola cipher ID {header[6]} ({GetCipherName(header[6])}), "
                + $"but the supplied options specify cipher ID {expected.CipherId} ({GetCipherName(expected.CipherId)}); "
                + "cipher fallback is not permitted.");
        }
        if (header[7..AhtolaHeaderSize].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Ahtola encrypted database header has non-zero reserved bytes.");

        // A database written with a wider nonce reserves more bytes per page. Left
        // unchecked, a reserved-space mismatch would only surface as a per-page
        // authentication failure, so reject it while the header is being read.
        if (header.Length > ReservedSpaceOffset && header[ReservedSpaceOffset] != expected.MetadataSize)
        {
            throw new InvalidDataException(
                $"Encrypted database reserves {header[ReservedSpaceOffset]} bytes per page, but cipher ID "
                + $"{expected.CipherId} ({GetCipherName(expected.CipherId)}) requires {expected.MetadataSize}; "
                + "cipher fallback is not permitted.");
        }
    }

    /// <summary>
    /// Rejects plaintext pages that already use the reserved bytes the encrypted
    /// layout needs for its tag and nonce.
    /// </summary>
    internal static void ValidatePlaintextReservedBytes(
        ReadOnlySpan<byte> page,
        uint pageNumber,
        in AhtolaCipherParameters parameters)
    {
        var metadataSize = parameters.MetadataSize;
        if (page[^metadataSize..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                $"Plaintext page {pageNumber} uses the {metadataSize} SQLite reserved bytes required for Ahtola encryption metadata.");
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
