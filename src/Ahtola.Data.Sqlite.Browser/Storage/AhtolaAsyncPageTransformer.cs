using System.Runtime.Versioning;
using System.Security.Cryptography;
using Ahtola.Core.Storage;

namespace Ahtola.Data.Sqlite.Browser.Storage;

/// <summary>
/// Bridges <see cref="AhtolaBrowserCryptoService"/> (Web Crypto) to the
/// asynchronous page cipher used by encrypted OPFS persistence.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class AhtolaBrowserWebCryptoPageCipher(AhtolaBrowserCryptoService service)
    : IAhtolaAsyncPageCipher
{
    /// <inheritdoc />
    public Core.Storage.AhtolaEncryptionCipher Cipher
        => AhtolaBrowserCryptoParameters.ToStorageCipher(service.Cipher);

    /// <inheritdoc />
    public async ValueTask<AhtolaBrowserAesGcmResult> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await service.EncryptAsync(plaintext, nonce, associatedData).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await service
            .DecryptAsync(ciphertext, tag, nonce, associatedData)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => service.DisposeAsync();

    internal static async ValueTask<IAhtolaAsyncPageCipher> CreateAsync(
        AhtolaBrowserEncryptionOptions encryption)
    {
        var service = await encryption.CreateCryptoServiceAsync().ConfigureAwait(false);
        try
        {
            return new AhtolaBrowserWebCryptoPageCipher(service);
        }
        catch
        {
            await service.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Encrypts and decrypts complete SQLite pages in Ahtola's AHTLA version 0
/// format using an asynchronous AES-GCM primitive.
/// </summary>
/// <remarks>
/// The produced bytes are identical to the desktop
/// <c>AhtolaPageEncryption</c> output: page 1 keeps a visible 100-byte header
/// beginning with the AHTLA magic and authenticates it as associated data,
/// every page stores a 16-byte tag followed by a 12-byte nonce in the
/// <see cref="AhtolaEncryptedPageFormat.MetadataSize"/> reserved bytes, and no
/// other page carries associated data.
/// </remarks>
internal sealed class AhtolaAsyncPageTransformer(IAhtolaAsyncPageCipher cipher) : IAsyncDisposable
{
    /// <summary>Reserved bytes every encrypted page requires.</summary>
    internal const int ReservedBytes = AhtolaEncryptedPageFormat.MetadataSize;

    /// <summary>The AHTLA cipher id recorded in encrypted page 1 headers.</summary>
    internal Core.Storage.AhtolaEncryptionCipher Cipher => cipher.Cipher;

    internal ValueTask<AhtolaBrowserAesGcmResult> EncryptLogicalLogChunkAsync(
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
        => cipher.EncryptAsync(plaintext, nonce, associatedData, cancellationToken);

    internal ValueTask<byte[]> DecryptLogicalLogChunkAsync(
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken)
        => cipher.DecryptAsync(ciphertext, tag, nonce, associatedData, cancellationToken);

    /// <summary>Encrypts one whole plaintext page.</summary>
    internal async ValueTask<byte[]> EncryptPageAsync(
        ReadOnlyMemory<byte> page,
        uint pageNumber,
        CancellationToken cancellationToken)
    {
        var pageSize = ValidatePage(page.Length, pageNumber);
        AhtolaEncryptedPageFormat.ValidatePlaintextReservedBytes(page.Span, pageNumber);

        var encrypted = new byte[pageSize];
        if (pageNumber == 1)
            AhtolaEncryptedPageFormat.WriteEncryptedHeaderPrefix(encrypted, page.Span, Cipher);

        var regions = AhtolaEncryptedPageFormat.Describe(pageSize, pageNumber);
        var nonce = new byte[AhtolaEncryptedPageFormat.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        nonce.CopyTo(encrypted.AsSpan(regions.NonceOffset));

        var associatedData = encrypted
            .AsMemory(regions.AssociatedDataOffset, regions.AssociatedDataLength);
        var result = await cipher
            .EncryptAsync(
                page.Slice(regions.PayloadOffset, regions.PayloadLength),
                nonce,
                associatedData,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Ciphertext.Length != regions.PayloadLength
            || result.Tag.Length != AhtolaEncryptedPageFormat.TagSize)
        {
            throw new CryptographicException(
                "The asynchronous AES-GCM provider returned an unexpected ciphertext or tag length.");
        }

        result.Ciphertext.CopyTo(encrypted.AsSpan(regions.PayloadOffset));
        result.Tag.CopyTo(encrypted.AsSpan(regions.TagOffset));
        return encrypted;
    }

    /// <summary>Authenticates and decrypts one whole encrypted page.</summary>
    internal async ValueTask<byte[]> DecryptPageAsync(
        ReadOnlyMemory<byte> encryptedPage,
        uint pageNumber,
        CancellationToken cancellationToken)
    {
        var pageSize = ValidatePage(encryptedPage.Length, pageNumber);
        if (pageNumber == 1)
            AhtolaEncryptedPageFormat.ValidateEncryptedHeader(encryptedPage.Span, Cipher);

        var plaintext = new byte[pageSize];
        if (pageNumber == 1)
            AhtolaEncryptedPageFormat.RestorePlaintextHeaderPrefix(plaintext, encryptedPage.Span);

        var regions = AhtolaEncryptedPageFormat.Describe(pageSize, pageNumber);
        byte[] payload;
        try
        {
            payload = await cipher
                .DecryptAsync(
                    encryptedPage.Slice(regions.PayloadOffset, regions.PayloadLength),
                    encryptedPage.Slice(regions.TagOffset, AhtolaEncryptedPageFormat.TagSize),
                    encryptedPage.Slice(regions.NonceOffset, AhtolaEncryptedPageFormat.NonceSize),
                    encryptedPage.Slice(regions.AssociatedDataOffset, regions.AssociatedDataLength),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CryptographicException exception)
        {
            throw AhtolaEncryptedPageFormat.CreateAuthenticationFailure(pageNumber, exception);
        }

        try
        {
            if (payload.Length != regions.PayloadLength)
                throw AhtolaEncryptedPageFormat.CreateAuthenticationFailure(pageNumber, inner: null);

            payload.CopyTo(plaintext.AsSpan(regions.PayloadOffset));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static int ValidatePage(int length, uint pageNumber)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        _ = SqlitePageSize.Encode(length);
        AhtolaEncryptedPageFormat.ValidatePageSize(length);
        return length;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => cipher.DisposeAsync();
}
