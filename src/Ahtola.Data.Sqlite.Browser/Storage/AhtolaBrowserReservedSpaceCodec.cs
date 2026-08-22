using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Reserves the bytes Ahtola page encryption needs without transforming page
/// content, so the browser engine keeps operating on plaintext while OPFS
/// persistence encrypts asynchronously through Web Crypto.
/// </summary>
/// <remarks>
/// <para>
/// Web Crypto is promise-based, so it cannot satisfy the synchronous
/// <see cref="IPageCodec"/> contract that the desktop encryption codec
/// implements. Binding this codec still forces the pager to create databases
/// with <see cref="AhtolaEncryptedPageFormat.MetadataSize"/> reserved bytes per
/// page from creation onward, and to reject an existing database whose reserved
/// space does not match, which is exactly the layout the asynchronous transform
/// needs for the 16-byte tag and 12-byte nonce it stores per page.
/// </para>
/// <para>
/// The identity transform is deliberate: pages, WAL frames, and journal records
/// stay plaintext inside the in-memory mirror and are encrypted only on their way
/// to durable OPFS storage.
/// </para>
/// </remarks>
internal sealed class AhtolaBrowserReservedSpaceCodec : IPageCodec
{
    private static readonly PageCodecId Id = CreateId();

    /// <inheritdoc />
    public PageCodecId CodecId => Id;

    /// <inheritdoc />
    public byte RequiredReservedBytes => checked((byte)AhtolaEncryptedPageFormat.MetadataSize);

    /// <inheritdoc />
    public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => input.CopyTo(output);

    /// <inheritdoc />
    public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => input.CopyTo(output);

    private static PageCodecId CreateId()
    {
        Span<byte> id = stackalloc byte[16];
        "ahtola-browser-r"u8.CopyTo(id);
        id[15] = AhtolaEncryptedPageFormat.MetadataSize;
        return new PageCodecId(id);
    }
}
