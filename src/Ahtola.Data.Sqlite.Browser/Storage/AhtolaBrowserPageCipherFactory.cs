using System.Runtime.Versioning;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Chooses the asynchronous page cipher for a browser database: Web Crypto for
/// the AES-GCM ciphers, the pure-managed AEGIS core for cipher IDs 3 through 8.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class AhtolaBrowserPageCipherFactory
{
    internal static async ValueTask<IAhtolaAsyncPageCipher> CreateAsync(
        AhtolaBrowserEncryptionOptions encryption)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        if (AhtolaBrowserCryptoParameters.UsesWebCrypto(encryption.Cipher))
            return await AhtolaBrowserWebCryptoPageCipher.CreateAsync(encryption).ConfigureAwait(false);

        return encryption.CreateManagedAegisPageCipher();
    }
}
