namespace Ahtola.Data.Sqlite.Browser;

/// <summary>An AES-GCM ciphertext and its separate AHTLA authentication tag.</summary>
public sealed class AhtolaBrowserAesGcmResult
{
    internal AhtolaBrowserAesGcmResult(byte[] ciphertext, byte[] tag)
    {
        Ciphertext = ciphertext;
        Tag = tag;
    }

    /// <summary>The encrypted bytes, excluding the authentication tag.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>The 16-byte authentication tag.</summary>
    public byte[] Tag { get; }
}
