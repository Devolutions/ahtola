using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Codecs;

/// <summary>
/// Reads and writes databases encrypted with wxSQLite3's <c>aes128cbc</c> cipher (wxSQLite3 4.x
/// "sqlite3secure", also the default AES-128 format of SQLite3 Multiple Ciphers' wxSQLite3
/// compatibility mode).
/// </summary>
/// <remarks>
/// <para>
/// The key is derived from the UTF-8 password with the PDF-style MD5/RC4 scheme (password padded
/// to 32 bytes, 50 MD5 rounds and 20 RC4 rounds). Each page is AES-128-CBC encrypted without
/// padding under <c>MD5(key ‖ LE32(page) ‖ "sAlT")</c>, with an IV that is the MD5 of four
/// <c>ModMult</c> words seeded with <c>page + 1</c>.
/// </para>
/// <para>
/// Page 1 keeps bytes 16 to 23 of the header (page size, file format versions, reserved space,
/// payload fractions) in clear so the page size can be read before the key is known; their
/// ciphertext moves to bytes 8 to 15. Files written in wxSQLite3's legacy mode encrypt the whole
/// first page and are read too. Pages carry no authentication: a wrong password is only detected
/// when the decoded header is not a SQLite header.
/// </para>
/// <para>
/// MD5, RC4 and unauthenticated CBC are kept only to open existing files. Prefer the built-in
/// authenticated encryption for new databases. The codec is thread-safe; dispose it to release
/// its AES instance once no connection uses it.
/// </para>
/// </remarks>
public sealed class WxSQLite3Aes128PageCodec : IPageCodec, IDisposable
{
    private const int BlockLength = 16;
    private const int KeyLength = 16;
    private const int MaxPasswordLength = 32;
    private const int MaxPageSize = 65536;
    private const int VisibleHeaderOffset = 16;
    private const int VisibleHeaderLength = 8;

    private static readonly PageCodecId Id = new("ahtola-wx-aes128"u8);

    private static ReadOnlySpan<byte> PasswordPadding =>
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static ReadOnlySpan<byte> PageKeySalt => "sAlT"u8;

    private readonly object _aesGate = new();
    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>Creates the codec for <paramref name="password"/>.</summary>
    public WxSQLite3Aes128PageCodec(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        _key = DeriveKey(Encoding.UTF8.GetBytes(password));
    }

    /// <inheritdoc />
    public PageCodecId CodecId => Id;

    /// <inheritdoc />
    public byte RequiredReservedBytes => 0;

    /// <inheritdoc />
    public PageCodecHeaderInfo BootstrapPageInfo(ReadOnlySpan<byte> rawPage1Prefix)
    {
        if (MatchesHeader(rawPage1Prefix) == false)
            throw new PageCodecKeyMismatchException();

        if (HasVisibleHeader(rawPage1Prefix))
            return PageCodecHeaderInfo.FromVisibleSqliteHeader(rawPage1Prefix);

        // Legacy mode encrypts the whole first page, header included.
        var length = rawPage1Prefix.Length - (rawPage1Prefix.Length % BlockLength);
        var decoded = new byte[length];
        Transform(1, encrypt: false, rawPage1Prefix[..length], decoded);
        return PageCodecHeaderInfo.FromVisibleSqliteHeader(decoded);
    }

    /// <inheritdoc />
    public bool? MatchesHeader(ReadOnlySpan<byte> rawPage1Prefix)
    {
        if (rawPage1Prefix.Length < VisibleHeaderOffset + BlockLength)
            return null;

        Span<byte> decoded = stackalloc byte[BlockLength];
        if (HasVisibleHeader(rawPage1Prefix))
        {
            // Bytes 8..15 hold the ciphertext of header bytes 16..23, which are also in clear:
            // the first block of the separately encrypted remainder of the page decodes to them.
            Span<byte> block = stackalloc byte[BlockLength];
            rawPage1Prefix.Slice(VisibleHeaderLength, VisibleHeaderLength).CopyTo(block);
            rawPage1Prefix.Slice(VisibleHeaderOffset + VisibleHeaderLength, VisibleHeaderLength).CopyTo(block[VisibleHeaderLength..]);
            Transform(1, encrypt: false, block, decoded);
            return decoded[..VisibleHeaderLength].SequenceEqual(
                rawPage1Prefix.Slice(VisibleHeaderOffset, VisibleHeaderLength));
        }

        Transform(1, encrypt: false, rawPage1Prefix[..BlockLength], decoded);
        return LegacyPageCodecs.HasSqliteMagic(decoded);
    }

    /// <inheritdoc />
    public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (context.PageNumber != 1)
        {
            Transform(context.PageNumber, encrypt: false, input, output);
            return;
        }

        var page = input.ToArray();
        if (!HasVisibleHeader(page))
        {
            Transform(1, encrypt: false, page, output);
            return;
        }

        var visibleHeader = page.AsSpan(VisibleHeaderOffset, VisibleHeaderLength).ToArray();
        page.AsSpan(VisibleHeaderLength, VisibleHeaderLength).CopyTo(page.AsSpan(VisibleHeaderOffset));
        Transform(1, encrypt: false, page.AsSpan(VisibleHeaderOffset), output[VisibleHeaderOffset..]);
        if (output.Slice(VisibleHeaderOffset, VisibleHeaderLength).SequenceEqual(visibleHeader))
        {
            // The first 16 bytes are the SQLite magic, which wxSQLite3 encrypts separately.
            LegacyPageCodecs.SqliteMagic.CopyTo(output);
        }
        else
        {
            page.AsSpan(0, VisibleHeaderOffset).CopyTo(output);
        }
    }

    /// <inheritdoc />
    public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (context.PageNumber != 1)
        {
            Transform(context.PageNumber, encrypt: true, input, output);
            return;
        }

        // The first 16 bytes and the rest of the page are encrypted separately; the encrypted
        // bytes 16..23 then move to 8..15 so the page size and reserved space stay readable.
        Transform(1, encrypt: true, input[..VisibleHeaderOffset], output[..VisibleHeaderOffset]);
        Transform(1, encrypt: true, input[VisibleHeaderOffset..], output[VisibleHeaderOffset..]);
        output.Slice(VisibleHeaderOffset, VisibleHeaderLength).CopyTo(output.Slice(VisibleHeaderLength, VisibleHeaderLength));
        input.Slice(VisibleHeaderOffset, VisibleHeaderLength).CopyTo(output.Slice(VisibleHeaderOffset, VisibleHeaderLength));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_aesGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _aes.Dispose();
        }
    }

    private static byte[] DeriveKey(byte[] password)
    {
        var userPad = PadPassword(password);
        var ownerPad = PadPassword([]);

        var digest = MD5.HashData(ownerPad);
        for (var i = 0; i < 50; i++)
            digest = MD5.HashData(digest);

        var ownerKey = (byte[])userPad.Clone();
        var roundKey = new byte[KeyLength];
        for (var i = 0; i < 20; i++)
        {
            for (var j = 0; j < KeyLength; j++)
                roundKey[j] = (byte)(digest[j] ^ i);
            LegacyRc4.Apply(roundKey, ownerKey, ownerKey);
        }

        digest = MD5.HashData([.. userPad, .. ownerKey]);
        for (var i = 0; i < 50; i++)
            digest = MD5.HashData(digest);

        return digest;
    }

    private static byte[] GetInitialVector(uint pageNumber)
    {
        var seed = new byte[BlockLength];
        var z = unchecked((int)pageNumber + 1);
        for (var i = 0; i < 4; i++)
        {
            var q = z / 52774;
            z = (40692 * (z - (52774 * q))) - (3791 * q);
            if (z < 0)
                z += 2147483399;
            BinaryPrimitives.WriteInt32LittleEndian(seed.AsSpan(i * 4), z);
        }

        return MD5.HashData(seed);
    }

    private static bool HasVisibleHeader(ReadOnlySpan<byte> page)
    {
        if (page.Length < VisibleHeaderOffset + VisibleHeaderLength)
            return false;

        var header = page.Slice(VisibleHeaderOffset, VisibleHeaderLength);
        var pageSize = (header[0] << 8) | (header[1] << 16);
        return pageSize is >= 512 and <= MaxPageSize
               && (pageSize & (pageSize - 1)) == 0
               && header[5] == 0x40
               && header[6] == 0x20
               && header[7] == 0x20;
    }

    private static byte[] PadPassword(ReadOnlySpan<byte> password)
    {
        var padded = new byte[MaxPasswordLength];
        var length = Math.Min(password.Length, MaxPasswordLength);
        password[..length].CopyTo(padded);
        PasswordPadding[..(MaxPasswordLength - length)].CopyTo(padded.AsSpan(length));
        return padded;
    }

    private byte[] GetPageKey(uint pageNumber)
    {
        Span<byte> material = stackalloc byte[KeyLength + 4 + 4];
        _key.CopyTo(material);
        BinaryPrimitives.WriteUInt32LittleEndian(material[KeyLength..], pageNumber);
        PageKeySalt.CopyTo(material[(KeyLength + 4)..]);
        return MD5.HashData(material);
    }

    private void Transform(uint pageNumber, bool encrypt, ReadOnlySpan<byte> input, Span<byte> output)
    {
        var pageKey = GetPageKey(pageNumber);
        var initialVector = GetInitialVector(pageNumber);
        lock (_aesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _aes.Key = pageKey;
            if (encrypt)
                _aes.EncryptCbc(input, initialVector, output, PaddingMode.None);
            else
                _aes.DecryptCbc(input, initialVector, output, PaddingMode.None);
        }
    }
}
