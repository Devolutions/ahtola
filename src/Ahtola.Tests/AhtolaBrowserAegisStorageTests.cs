using System.Security.Cryptography;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite.Browser;
using Ahtola.Data.Sqlite.Browser.Storage;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

#pragma warning disable CA1416

namespace Ahtola.Tests;

/// <summary>
/// Proves the browser package writes byte-identical AEGIS pages to the desktop
/// engine.
/// </summary>
/// <remarks>
/// SubtleCrypto has no AEGIS, so the browser routes cipher IDs 3 through 8
/// through <see cref="AhtolaManagedAegisPageCipher"/>, which wraps the same
/// pure-managed core <c>Ahtola.Core</c> uses. That makes the two implementations
/// share an AEAD but not a page framer, so these tests still have something real
/// to check: the reserved-byte count, the page-1 header, and the
/// <c>ciphertext || tag || nonce</c> layout are produced independently by
/// <see cref="AhtolaAsyncPageTransformer"/>.
/// </remarks>
[NonParallelizable]
public sealed class AhtolaBrowserAegisStorageTests
{
    private const string Key128 = "000102030405060708090A0B0C0D0E0F";
    private const string Key256 = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    public static IEnumerable<TestCaseData> AegisCiphers()
    {
        yield return new TestCaseData(StorageCipher.Aegis256, Key256, (byte)48).SetName("{m}(Aegis256)");
        yield return new TestCaseData(StorageCipher.Aegis256X2, Key256, (byte)48).SetName("{m}(Aegis256X2)");
        yield return new TestCaseData(StorageCipher.Aegis256X4, Key256, (byte)48).SetName("{m}(Aegis256X4)");
        yield return new TestCaseData(StorageCipher.Aegis128L, Key128, (byte)32).SetName("{m}(Aegis128L)");
        yield return new TestCaseData(StorageCipher.Aegis128X2, Key128, (byte)32).SetName("{m}(Aegis128X2)");
        yield return new TestCaseData(StorageCipher.Aegis128X4, Key128, (byte)32).SetName("{m}(Aegis128X4)");
    }

    [TestCaseSource(nameof(AegisCiphers))]
    public async Task BrowserAndDesktopProduceInterchangeablePages(
        StorageCipher cipher,
        string hexKey,
        byte reservedBytes)
    {
        const int PageSize = 4096;
        var key = Convert.FromHexString(hexKey);
        await using var transformer = new AhtolaAsyncPageTransformer(
            new AhtolaManagedAegisPageCipher(cipher, key));
        using var options = new AhtolaEncryptionOptions(cipher, key);
        using var desktop = options.CreatePageEncryption(PageSize);

        transformer.ReservedBytes.Should().Be(reservedBytes);

        foreach (var pageNumber in new uint[] { 1, 2, 37 })
        {
            var plaintext = CreatePlaintextPage(PageSize, pageNumber, reservedBytes);

            var browserEncrypted = await transformer.EncryptPageAsync(plaintext, pageNumber, default);
            desktop.DecryptPage(browserEncrypted, pageNumber).Should().Equal(
                plaintext, "the desktop engine must decrypt a page the browser wrote");

            var desktopEncrypted = desktop.EncryptPage(plaintext, pageNumber);
            (await transformer.DecryptPageAsync(desktopEncrypted, pageNumber, default)).Should().Equal(
                plaintext, "the browser must decrypt a page the desktop engine wrote");

            if (pageNumber != 1)
                continue;

            System.Text.Encoding.ASCII.GetString(browserEncrypted, 0, 5).Should().Be("AHTLA");
            browserEncrypted[5].Should().Be(0);
            browserEncrypted[6].Should().Be((byte)cipher, "the page-1 header records the Turso cipher id");
            browserEncrypted.AsSpan(16, 84).ToArray().Should().Equal(
                plaintext.AsSpan(16, 84).ToArray(),
                "the visible SQLite header tail is copied verbatim and authenticated");
        }
    }

    /// <summary>
    /// A page written by the browser transformer with a pinned nonce must be
    /// byte-for-byte what the desktop framer produces from the same inputs.
    /// </summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public async Task BrowserFramesAreByteIdenticalToDesktopFrames(
        StorageCipher cipher,
        string hexKey,
        byte reservedBytes)
    {
        const int PageSize = 1024;
        var key = Convert.FromHexString(hexKey);
        await using var transformer = new AhtolaAsyncPageTransformer(
            new AhtolaManagedAegisPageCipher(cipher, key));

        foreach (var pageNumber in new uint[] { 1, 5 })
        {
            var plaintext = CreatePlaintextPage(PageSize, pageNumber, reservedBytes);
            var browserEncrypted = await transformer.EncryptPageAsync(plaintext, pageNumber, default);

            // Replay the desktop framer with the nonce the browser actually chose.
            var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
            var nonce = browserEncrypted.AsSpan(PageSize - parameters.NonceSize, parameters.NonceSize).ToArray();
            var desktopEncrypted = AhtolaPageCipherParityTests.EncryptWithFixedNonce(
                cipher, key, nonce, plaintext, pageNumber);

            browserEncrypted.Should().Equal(desktopEncrypted, $"{cipher} page {pageNumber}");
        }
    }

    /// <summary>Tampering must fail on the browser path exactly as it does on the desktop path.</summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public async Task BrowserRejectsTamperedAegisPages(
        StorageCipher cipher,
        string hexKey,
        byte reservedBytes)
    {
        const int PageSize = 1024;
        var key = Convert.FromHexString(hexKey);
        await using var transformer = new AhtolaAsyncPageTransformer(
            new AhtolaManagedAegisPageCipher(cipher, key));

        var plaintext = CreatePlaintextPage(PageSize, 3, reservedBytes);
        var encrypted = await transformer.EncryptPageAsync(plaintext, 3, default);

        foreach (var offset in new[] { 0, PageSize - reservedBytes, PageSize - reservedBytes + 8, PageSize - 1 })
        {
            var tampered = (byte[])encrypted.Clone();
            tampered[offset] ^= 0x01;
            var failure = Assert.ThrowsAsync<InvalidDataException>(
                async () => await transformer.DecryptPageAsync(tampered, 3, default))!;
            failure.Message.Should().Contain("failed authentication");
        }
    }

    /// <summary>The reserved-space codec must reserve this cipher's metadata size.</summary>
    [TestCaseSource(nameof(AegisCiphers))]
    public void BrowserReservedSpaceCodecMatchesTheCipher(
        StorageCipher cipher,
        string hexKey,
        byte reservedBytes)
    {
        _ = hexKey;
        var codec = new AhtolaBrowserReservedSpaceCodec(cipher);

        codec.RequiredReservedBytes.Should().Be(reservedBytes);
        codec.CodecId.IsZero.Should().BeFalse();

        var other = new AhtolaBrowserReservedSpaceCodec(StorageCipher.Aes256Gcm);
        codec.CodecId.Should().NotBe(other.CodecId, "different layouts must not share a codec id");
    }

    /// <summary>
    /// The browser package rejects ChaCha20-Poly1305 with the same reason the
    /// replica gate gives: it has no on-disk cipher id in Turso format version 0.
    /// </summary>
    [Test]
    public void BrowserRejectsCiphersWithNoOnDiskCipherId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AhtolaBrowserCryptoParameters.GetKeySize((AhtolaEncryptionCipher)999))!
            .Message.Should().Contain("ChaCha20-Poly1305 is a Turso Cloud server-side cipher");
    }

    /// <summary>Web Crypto backs the AES ciphers; everything else uses the managed core.</summary>
    [TestCase(AhtolaEncryptionCipher.Aes128Gcm, true)]
    [TestCase(AhtolaEncryptionCipher.Aes256Gcm, true)]
    [TestCase(AhtolaEncryptionCipher.Aegis256, false)]
    [TestCase(AhtolaEncryptionCipher.Aegis128l, false)]
    [TestCase(AhtolaEncryptionCipher.Aegis128x4, false)]
    [TestCase(AhtolaEncryptionCipher.Aegis256x4, false)]
    public void WebCryptoBacksOnlyTheAesCiphers(AhtolaEncryptionCipher cipher, bool expected)
        => AhtolaBrowserCryptoParameters.UsesWebCrypto(cipher).Should().Be(expected);

    /// <summary>Passphrase derivation stays AES-256-GCM, so it cannot produce an AEGIS cipher.</summary>
    [Test]
    public void PasswordDerivedOptionsCannotCreateAnAegisPageCipher()
    {
        using var options = AhtolaBrowserEncryptionOptions.FromPassword("correct horse battery staple");

        options.Cipher.Should().Be(AhtolaEncryptionCipher.Aes256Gcm);
        Assert.Throws<InvalidOperationException>(() => options.CreateManagedAegisPageCipher());
    }

    private static byte[] CreatePlaintextPage(int pageSize, uint pageNumber, byte reservedBytes)
    {
        var page = new byte[pageSize];
        if (pageNumber == 1)
        {
            "SQLite format 3\0"u8.CopyTo(page);
            page[16] = (byte)(pageSize >> 8);
            page[17] = (byte)pageSize;
            page[18] = 2;
            page[19] = 2;
            page[20] = reservedBytes;
        }

        for (var index = pageNumber == 1 ? 21 : 0; index < pageSize - reservedBytes; index++)
            page[index] = (byte)((index * 31) + pageNumber);
        return page;
    }
}
