using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Reserves the bytes Ahtola page encryption needs without transforming page
/// content, so the browser engine keeps operating on plaintext while OPFS
/// persistence encrypts asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// Web Crypto is promise-based, so it cannot satisfy the synchronous
/// <see cref="IPageCodec"/> contract that the desktop encryption codec
/// implements. Binding this codec still forces the pager to create databases
/// with the configured cipher's reserved-byte count from creation onward, and to
/// reject an existing database whose reserved space does not match, which is
/// exactly the layout the asynchronous transform needs for the 16-byte tag and
/// the cipher's nonce.
/// </para>
/// <para>
/// The reserved-byte count is cipher-dependent (28 for AES-GCM, 32 for the
/// AEGIS-128 family, 48 for the AEGIS-256 family), so the codec identity embeds
/// it: two configurations that produce different on-disk layouts must never
/// share a <see cref="PageCodecId"/>.
/// </para>
/// <para>
/// The identity transform is deliberate: pages, WAL frames, and journal records
/// stay plaintext inside the in-memory mirror and are encrypted only on their way
/// to durable OPFS storage.
/// </para>
/// </remarks>
internal sealed class AhtolaBrowserReservedSpaceCodec : IPageCodec
{
    private readonly PageCodecId _codecId;

    internal AhtolaBrowserReservedSpaceCodec(Core.Storage.AhtolaEncryptionCipher cipher)
    {
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        RequiredReservedBytes = checked((byte)parameters.MetadataSize);

        Span<byte> id = stackalloc byte[16];
        "ahtola-browser-"u8.CopyTo(id);
        id[15] = parameters.CipherId;
        _codecId = new PageCodecId(id);
    }

    /// <inheritdoc />
    public PageCodecId CodecId => _codecId;

    /// <inheritdoc />
    public byte RequiredReservedBytes { get; }

    /// <inheritdoc />
    public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => input.CopyTo(output);

    /// <inheritdoc />
    public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => input.CopyTo(output);
}
