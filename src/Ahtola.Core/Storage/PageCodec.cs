namespace Ahtola.Core.Storage;

/// <summary>
/// Page-layout fields recovered from the encoded prefix of page 1 before that
/// page can be decoded. Mirrors Turso <c>PageCodecHeaderInfo</c>.
/// </summary>
public readonly record struct PageCodecHeaderInfo(int PageSize, byte ReservedSpace)
{
    /// <summary>Minimum prefix length needed to read SQLite page-size and reserved-space fields.</summary>
    public const int SqliteBootstrapHeaderLength = 21;

    /// <summary>
    /// Reads the SQLite page-layout fields that must remain visible before page 1
    /// can be decoded. Codecs that transform those bytes must override
    /// <see cref="IPageCodec.BootstrapPageInfo"/>.
    /// </summary>
    public static PageCodecHeaderInfo FromVisibleSqliteHeader(ReadOnlySpan<byte> rawPage1Prefix)
    {
        if (rawPage1Prefix.Length < SqliteBootstrapHeaderLength)
        {
            throw new ArgumentException(
                $"Page codec bootstrap requires at least {SqliteBootstrapHeaderLength} bytes, got {rawPage1Prefix.Length}.",
                nameof(rawPage1Prefix));
        }

        var rawPageSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(rawPage1Prefix[16..]);
        var pageSize = rawPageSize == 1 ? 65_536 : rawPageSize;
        _ = SqlitePageSize.Encode(pageSize);
        return new PageCodecHeaderInfo(pageSize, rawPage1Prefix[20]);
    }
}

/// <summary>Whether a transformed page image belongs to the main database or WAL.</summary>
public enum PageLocation : byte
{
    Database = 0,
    Wal = 1,
}

/// <summary>Context supplied to page codec encode/decode callbacks.</summary>
public readonly record struct PageCodecContext(uint PageNumber, PageLocation Location)
{
    /// <summary>Creates a context for a one-based SQLite page number.</summary>
    public static PageCodecContext Create(uint pageNumber, PageLocation location)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        if (location is not PageLocation.Database and not PageLocation.Wal)
            throw new ArgumentOutOfRangeException(nameof(location), location, "Unsupported page codec location.");
        return new PageCodecContext(pageNumber, location);
    }
}

/// <summary>
/// A stable, non-secret identifier for a page codec configuration.
/// Must change whenever the codec could produce different on-disk bytes.
/// Do not embed secrets.
/// </summary>
public readonly struct PageCodecId : IEquatable<PageCodecId>
{
    private readonly ulong _lo;
    private readonly ulong _hi;

    /// <summary>Creates an identifier from exactly 16 bytes.</summary>
    public PageCodecId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("Page codec identifiers are exactly 16 bytes.", nameof(bytes));
        _lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }

    /// <summary>True when every byte is zero (invalid for external codecs).</summary>
    public bool IsZero => _lo == 0 && _hi == 0;

    /// <summary>Copies the 16-byte identifier into <paramref name="destination"/>.</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("Destination must be at least 16 bytes.", nameof(destination));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, _lo);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _hi);
    }

    /// <summary>Returns a new 16-byte array containing this identifier.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[16];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>Rejects the all-zero identifier used by the C ABI / managed external codec contract.</summary>
    public static void ValidateNonZero(PageCodecId codecId)
    {
        if (codecId.IsZero)
        {
            throw new ArgumentException(
                "Page codec codec_id must be a stable non-zero identifier.",
                nameof(codecId));
        }
    }

    /// <inheritdoc />
    public bool Equals(PageCodecId other) => _lo == other._lo && _hi == other._hi;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PageCodecId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_lo, _hi);

    /// <summary>Equality.</summary>
    public static bool operator ==(PageCodecId left, PageCodecId right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(PageCodecId left, PageCodecId right) => !left.Equals(right);
}

/// <summary>
/// Converts complete SQLite page images between their on-disk and in-memory
/// representations. Codecs must preserve the fixed page size; per-page metadata
/// belongs in reserved bytes. Mirrors Turso <c>PageCodec</c>.
/// </summary>
public interface IPageCodec
{
    /// <summary>Stable, non-secret configuration fingerprint for this codec.</summary>
    PageCodecId CodecId { get; }

    /// <summary>Exact reserved-byte count required in every page.</summary>
    byte RequiredReservedBytes { get; }

    /// <summary>
    /// Reports page layout metadata from the encoded prefix of page 1.
    /// Default-compatible codecs leave SQLite page-size/reserved fields visible.
    /// </summary>
    PageCodecHeaderInfo BootstrapPageInfo(ReadOnlySpan<byte> rawPage1Prefix)
        => PageCodecHeaderInfo.FromVisibleSqliteHeader(rawPage1Prefix);

    /// <summary>Encodes a plaintext SQLite page into on-disk bytes.</summary>
    void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output);

    /// <summary>Decodes on-disk bytes into a plaintext SQLite page.</summary>
    void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output);
}

/// <summary>Helpers for resolving and applying page codecs in the storage layer.</summary>
internal static class PageCodecSupport
{
    internal static void RejectCombinedTransforms(AhtolaEncryptionOptions? encryption, IPageCodec? pageCodec)
    {
        if (encryption is not null && pageCodec is not null)
        {
            throw new ArgumentException(
                "Built-in encryption cannot be combined with an external page codec.");
        }
    }

    internal static void ValidateExternalCodec(IPageCodec pageCodec)
    {
        ArgumentNullException.ThrowIfNull(pageCodec);
        PageCodecId.ValidateNonZero(pageCodec.CodecId);
    }

    internal static IPageCodec? Bind(
        AhtolaEncryptionOptions? encryption,
        IPageCodec? pageCodec,
        int pageSize,
        out bool ownsCodec)
    {
        RejectCombinedTransforms(encryption, pageCodec);
        if (encryption is not null)
        {
            ownsCodec = true;
            return EncryptionPageCodec.Create(encryption, pageSize);
        }

        if (pageCodec is not null)
        {
            ValidateExternalCodec(pageCodec);
            ownsCodec = false;
            return pageCodec;
        }

        ownsCodec = false;
        return null;
    }

    internal static void Encode(
        IPageCodec codec,
        PageLocation location,
        uint pageNumber,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        if (input.Length != output.Length)
        {
            throw new ArgumentException(
                $"Page codec encode requires equal input/output lengths; got {input.Length} and {output.Length}.");
        }

        codec.EncodePage(PageCodecContext.Create(pageNumber, location), input, output);
    }

    internal static void Decode(
        IPageCodec codec,
        PageLocation location,
        uint pageNumber,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        if (input.Length != output.Length)
        {
            throw new ArgumentException(
                $"Page codec decode requires equal input/output lengths; got {input.Length} and {output.Length}.");
        }

        codec.DecodePage(PageCodecContext.Create(pageNumber, location), input, output);
    }

    internal static byte[] EncodeToArray(
        IPageCodec codec,
        PageLocation location,
        uint pageNumber,
        ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        Encode(codec, location, pageNumber, input, output);
        return output;
    }

    internal static void DisposeOwned(IPageCodec? codec, bool ownsCodec)
    {
        if (ownsCodec && codec is IDisposable disposable)
            disposable.Dispose();
    }

    internal static SqliteDatabaseHeader ApplyReservedBytes(IPageCodec codec, SqliteDatabaseHeader header)
    {
        if (codec is EncryptionPageCodec encryptionCodec)
            return encryptionCodec.PrepareHeader(header);

        var reserved = codec.RequiredReservedBytes;
        if (header.PageSize - reserved < SqliteDatabaseHeader.MinimumUsableSpace)
            throw new InvalidOperationException("Page codec reserved bytes leave too little usable SQLite page space.");

        return header with { ReservedSpace = reserved };
    }
}

/// <summary>
/// Built-in Ahtola AES-GCM page encryption implemented as an <see cref="IPageCodec"/>.
/// Mirrors Turso <c>EncryptionPageCodec</c>.
/// </summary>
internal sealed class EncryptionPageCodec : IPageCodec, IDisposable
{
    private readonly AhtolaPageEncryption _encryption;
    private readonly PageCodecId _codecId;
    private bool _disposed;

    private EncryptionPageCodec(AhtolaPageEncryption encryption)
    {
        _encryption = encryption;
        Span<byte> id = stackalloc byte[16];
        "ahtola-encrypt-v"u8.CopyTo(id);
        id[15] = (byte)encryption.Cipher;
        _codecId = new PageCodecId(id);
    }

    internal static EncryptionPageCodec Create(AhtolaEncryptionOptions options, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new EncryptionPageCodec(options.CreatePageEncryption(pageSize));
    }

    /// <inheritdoc />
    public PageCodecId CodecId => _codecId;

    /// <inheritdoc />
    public byte RequiredReservedBytes => checked((byte)_encryption.MetadataSize);

    internal AhtolaEncryptionCipher Cipher => _encryption.Cipher;

    internal int PageSize => _encryption.PageSize;

    internal SqliteDatabaseHeader PrepareHeader(SqliteDatabaseHeader header)
    {
        ThrowIfDisposed();
        return _encryption.PrepareHeader(header);
    }

    internal void ValidateEncryptedHeader(ReadOnlySpan<byte> header)
    {
        ThrowIfDisposed();
        _encryption.ValidateEncryptedHeader(header);
    }

    /// <inheritdoc />
    public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
    {
        ThrowIfDisposed();
        var encoded = _encryption.EncryptPage(input, context.PageNumber);
        encoded.CopyTo(output);
    }

    /// <inheritdoc />
    public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
    {
        ThrowIfDisposed();
        var decoded = _encryption.DecryptPage(input, context.PageNumber);
        decoded.CopyTo(output);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _encryption.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
