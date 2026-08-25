using System.Security.Cryptography;
using Ahtola.Core.Storage;
using Ahtola.Core.Storage.Crypto;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Backs the browser's asynchronous page cipher seam with the pure-managed AEGIS
/// core from <c>Ahtola.Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// SubtleCrypto exposes AES-GCM only, so there is no Web Crypto route to AEGIS.
/// Because the AEGIS implementation is pure managed it runs unchanged on
/// browser-wasm, and the resulting page bytes are identical to the ones the
/// desktop engine writes. Everything here is synchronous and simply completes
/// the <see cref="ValueTask"/> immediately; no JavaScript interop is involved.
/// </para>
/// <para>
/// Performance note: wasm has no AES round instruction
/// (<c>System.Runtime.Intrinsics.X86.Aes.IsSupported</c> and its Arm counterpart
/// are both false), so AEGIS falls back to the constant-time bitsliced software
/// round and is materially slower than AES-GCM through Web Crypto, which does
/// reach native code. Choose AEGIS in the browser for on-disk compatibility with
/// an AEGIS database, not for throughput.
/// </para>
/// </remarks>
internal sealed class AhtolaManagedAegisPageCipher : IAhtolaAsyncPageCipher
{
    private readonly IAhtolaAead _aead;
    private int _disposed;

    internal AhtolaManagedAegisPageCipher(Core.Storage.AhtolaEncryptionCipher cipher, ReadOnlySpan<byte> key)
    {
        Cipher = cipher;
        _aead = AhtolaAeadFactory.Create(cipher, key);
    }

    /// <inheritdoc />
    public Core.Storage.AhtolaEncryptionCipher Cipher { get; }

    /// <inheritdoc />
    public ValueTask<AhtolaBrowserAesGcmResult> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[_aead.TagSize];
        _aead.Encrypt(nonce.Span, plaintext.Span, ciphertext, tag, associatedData.Span);
        return ValueTask.FromResult(new AhtolaBrowserAesGcmResult(ciphertext, tag));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var plaintext = new byte[ciphertext.Length];
        if (!_aead.TryDecrypt(nonce.Span, ciphertext.Span, tag.Span, plaintext, associatedData.Span))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("AEGIS authentication failed.");
        }

        return ValueTask.FromResult(plaintext);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _aead.Dispose();
        return ValueTask.CompletedTask;
    }
}
