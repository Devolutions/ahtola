using System.Security.Cryptography;
using System.Text;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Codecs;

/// <summary>
/// Reads and writes databases encrypted by System.Data.SQLite's <c>Password</c> /
/// <c>ChangePassword</c> (its CryptoAPI RC4 format, used up to System.Data.SQLite 1.0.112).
/// </summary>
/// <remarks>
/// <para>
/// The key is the first 16 bytes of <c>SHA1(UTF-8 password)</c>. Every page, the database header
/// included, is RC4-encrypted from the start of the key stream; pages have no reserved bytes and
/// no authentication, so a wrong password is only detected when the decoded header is not a
/// SQLite header.
/// </para>
/// <para>
/// RC4 and SHA-1 are kept only to open existing files. Prefer the built-in authenticated
/// encryption (<c>Encryption Cipher</c>/<c>Encryption Key</c>) for new databases.
/// </para>
/// </remarks>
public sealed class SystemDataSQLiteRc4PageCodec : IPageCodec
{
    private const int KeyLength = 16;

    private static readonly PageCodecId Id = new("ahtola-sds-rc4-1"u8);

    private readonly byte[] _scheduledState;

    /// <summary>Creates the codec for <paramref name="password"/>.</summary>
    public SystemDataSQLiteRc4PageCodec(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var key = SHA1.HashData(Encoding.UTF8.GetBytes(password)).AsSpan(0, KeyLength);
        _scheduledState = LegacyRc4.ScheduleKey(key);
    }

    /// <inheritdoc />
    public PageCodecId CodecId => Id;

    /// <inheritdoc />
    public byte RequiredReservedBytes => 0;

    /// <inheritdoc />
    public PageCodecHeaderInfo BootstrapPageInfo(ReadOnlySpan<byte> rawPage1Prefix)
    {
        var decoded = new byte[rawPage1Prefix.Length];
        LegacyRc4.ApplyScheduled(_scheduledState, rawPage1Prefix, decoded);
        if (!LegacyPageCodecs.HasSqliteMagic(decoded))
            throw new PageCodecKeyMismatchException();
        return PageCodecHeaderInfo.FromVisibleSqliteHeader(decoded);
    }

    /// <inheritdoc />
    public bool? MatchesHeader(ReadOnlySpan<byte> rawPage1Prefix)
    {
        if (rawPage1Prefix.Length < LegacyPageCodecs.MagicLength)
            return null;

        Span<byte> decoded = stackalloc byte[LegacyPageCodecs.MagicLength];
        LegacyRc4.ApplyScheduled(_scheduledState, rawPage1Prefix[..decoded.Length], decoded);
        return LegacyPageCodecs.HasSqliteMagic(decoded);
    }

    /// <inheritdoc />
    public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => LegacyRc4.ApplyScheduled(_scheduledState, input, output);

    /// <inheritdoc />
    public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
        => LegacyRc4.ApplyScheduled(_scheduledState, input, output);
}
