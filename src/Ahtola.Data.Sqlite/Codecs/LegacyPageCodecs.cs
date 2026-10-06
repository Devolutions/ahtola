using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Codecs;

/// <summary>The password-based page formats written by native SQLite encryption extensions.</summary>
public enum LegacyPageCodecFormat
{
    /// <summary>A plain, unencrypted SQLite file.</summary>
    None = 0,

    /// <summary>System.Data.SQLite's RC4 format (<see cref="SystemDataSQLiteRc4PageCodec"/>).</summary>
    SystemDataSQLiteRc4 = 1,

    /// <summary>wxSQLite3's AES-128-CBC format (<see cref="WxSQLite3Aes128PageCodec"/>).</summary>
    WxSQLite3Aes128 = 2,
}

/// <summary>
/// Creates and detects the legacy password codecs, so products migrating off
/// System.Data.SQLite or wxSQLite3/sqlite3secure open their customers' files with one shared,
/// fixture-validated implementation.
/// </summary>
public static class LegacyPageCodecs
{
    internal const int MagicLength = 16;

    /// <summary>The number of leading file bytes detection reads.</summary>
    public const int DetectionPrefixLength = 32;

    internal static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    internal static bool HasSqliteMagic(ReadOnlySpan<byte> page) => page.StartsWith(SqliteMagic);

    /// <summary>Creates the codec for <paramref name="format"/> and <paramref name="password"/>.</summary>
    public static IPageCodec Create(LegacyPageCodecFormat format, string password)
        => format switch
        {
            LegacyPageCodecFormat.SystemDataSQLiteRc4 => new SystemDataSQLiteRc4PageCodec(password),
            LegacyPageCodecFormat.WxSQLite3Aes128 => new WxSQLite3Aes128PageCodec(password),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No codec exists for this format."),
        };

    /// <summary>
    /// Identifies which legacy format, keyed with <paramref name="password"/>, wrote the file
    /// that starts with <paramref name="filePrefix"/> (its first <see cref="DetectionPrefixLength"/>
    /// bytes). Returns <see langword="null"/> when the password matches no known format, and
    /// <see cref="LegacyPageCodecFormat.None"/> for a plain SQLite file.
    /// </summary>
    public static LegacyPageCodecFormat? Detect(ReadOnlySpan<byte> filePrefix, string password)
    {
        if (HasSqliteMagic(filePrefix))
            return LegacyPageCodecFormat.None;

        foreach (var format in new[] { LegacyPageCodecFormat.SystemDataSQLiteRc4, LegacyPageCodecFormat.WxSQLite3Aes128 })
        {
            var codec = Create(format, password);
            try
            {
                if (codec.MatchesHeader(filePrefix) == true)
                    return format;
            }
            finally
            {
                (codec as IDisposable)?.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the start of <paramref name="path"/> and identifies its legacy format (see
    /// <see cref="Detect(ReadOnlySpan{byte}, string)"/>). A missing or empty file returns
    /// <see langword="null"/>.
    /// </summary>
    public static LegacyPageCodecFormat? DetectFile(string path, string password)
    {
        var prefix = ReadPrefix(path);
        return prefix.Length == 0 ? null : Detect(prefix, password);
    }

    internal static byte[] ReadPrefix(string path)
    {
        if (!File.Exists(path))
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var prefix = new byte[DetectionPrefixLength];
        var read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        return prefix.AsSpan(0, read).ToArray();
    }
}
