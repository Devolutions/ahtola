using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// The asynchronous AES-GCM primitive used to build AHTLA pages. The browser
/// implementation is backed by Web Crypto, whose API is promise-based and can
/// therefore never satisfy the synchronous <see cref="IPageCodec"/> contract the
/// desktop engine uses.
/// </summary>
/// <remarks>
/// Keeping this seam abstract lets the managed test suite drive the exact same
/// page, WAL, and journal transforms with <c>System.Security.Cryptography</c> so
/// browser output can be proven byte-identical to desktop output.
/// </remarks>
internal interface IAhtolaAsyncPageCipher : IAsyncDisposable
{
    /// <summary>The AHTLA cipher id written into encrypted page 1 headers.</summary>
    Core.Storage.AhtolaEncryptionCipher Cipher { get; }

    /// <summary>Encrypts <paramref name="plaintext"/> and returns its ciphertext and tag.</summary>
    ValueTask<AhtolaBrowserAesGcmResult> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken);

    /// <summary>
    /// Authenticates and decrypts <paramref name="ciphertext"/>. Implementations
    /// throw <see cref="System.Security.Cryptography.CryptographicException"/>
    /// (or a subclass) when the tag does not match.
    /// </summary>
    ValueTask<byte[]> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken);
}
