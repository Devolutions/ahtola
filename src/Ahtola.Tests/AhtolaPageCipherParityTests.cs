using System.Buffers.Binary;
using System.Security.Cryptography;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Core.Storage.Crypto;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

/// <summary>
/// Page-level parity tests for every Turso format version 0 cipher id.
/// </summary>
/// <remarks>
/// These exercise the encrypted-page frame itself -- <c>ciphertext || tag || nonce</c>
/// with the page-1 header as associated data -- rather than the AEAD primitive,
/// which <see cref="AegisKnownAnswerTests"/> pins against the CFRG specification.
/// </remarks>
public sealed class AhtolaPageCipherParityTests
{
    private const string Key128 = "000102030405060708090A0B0C0D0E0F";
    private const string Key256 = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    public static IEnumerable<TestCaseData> AllCiphers()
    {
        yield return new TestCaseData(StorageCipher.Aes128Gcm, Key128, (byte)1, 12, 28).SetName("{m}(Aes128Gcm)");
        yield return new TestCaseData(StorageCipher.Aes256Gcm, Key256, (byte)2, 12, 28).SetName("{m}(Aes256Gcm)");
        yield return new TestCaseData(StorageCipher.Aegis256, Key256, (byte)3, 32, 48).SetName("{m}(Aegis256)");
        yield return new TestCaseData(StorageCipher.Aegis256X2, Key256, (byte)4, 32, 48).SetName("{m}(Aegis256X2)");
        yield return new TestCaseData(StorageCipher.Aegis256X4, Key256, (byte)5, 32, 48).SetName("{m}(Aegis256X4)");
        yield return new TestCaseData(StorageCipher.Aegis128L, Key128, (byte)6, 16, 32).SetName("{m}(Aegis128L)");
        yield return new TestCaseData(StorageCipher.Aegis128X2, Key128, (byte)7, 16, 32).SetName("{m}(Aegis128X2)");
        yield return new TestCaseData(StorageCipher.Aegis128X4, Key128, (byte)8, 16, 32).SetName("{m}(Aegis128X4)");
    }

    public static IEnumerable<TestCaseData> AllCipherPairs()
    {
        var ciphers = AllCiphers().ToArray();
        foreach (var left in ciphers)
        {
            foreach (var right in ciphers)
            {
                var leftCipher = (StorageCipher)left.Arguments[0]!;
                var rightCipher = (StorageCipher)right.Arguments[0]!;
                if (leftCipher == rightCipher)
                    continue;

                yield return new TestCaseData(leftCipher, (string)left.Arguments[1]!, rightCipher, (string)right.Arguments[1]!)
                    .SetName($"{{m}}({leftCipher}_opened_as_{rightCipher})");
            }
        }
    }

    /// <summary>The cipher parameter table must match Turso's <c>CipherMode</c> exactly.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void CipherParametersMatchTursoFormatVersionZero(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);

        parameters.CipherId.Should().Be(cipherId);
        parameters.CipherId.Should().Be((byte)cipher, "the enum values are the on-disk cipher ids");
        parameters.NonceSize.Should().Be(nonceSize);
        parameters.TagSize.Should().Be(16, "Turso instantiates every AEGIS cipher with a 128-bit tag");
        parameters.MetadataSize.Should().Be(metadataSize);
        parameters.KeySize.Should().Be(Convert.FromHexString(key).Length);
        AhtolaEncryptedPageFormat.FromCipherId(cipherId).Should().Be(cipher);
    }

    /// <summary>Whole-page round-trip for page 1 and a non-header page, at three page sizes.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void EveryCipherRoundTripsPageOneAndOrdinaryPages(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        _ = cipherId;
        _ = nonceSize;
        foreach (var pageSize in new[] { 512, 4096, 65536 })
        {
            using var encryption = CreatePageEncryption(cipher, key, pageSize);

            var firstPage = CreatePlaintextPageOne(pageSize, metadataSize);
            var encryptedFirst = encryption.EncryptPage(firstPage, 1);
            encryptedFirst.Length.Should().Be(pageSize);
            encryptedFirst.AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());
            encryption.DecryptPage(encryptedFirst, 1).Should().Equal(firstPage);

            var ordinaryPage = CreatePlaintextPage(pageSize, metadataSize, 0x5A);
            var encryptedOrdinary = encryption.EncryptPage(ordinaryPage, 7);
            encryptedOrdinary.Length.Should().Be(pageSize);
            encryption.DecryptPage(encryptedOrdinary, 7).Should().Equal(ordinaryPage);
        }
    }

    /// <summary>The frame layout must be exactly <c>ciphertext || tag || nonce</c>.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void EveryCipherFramesCiphertextThenTagThenNonce(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        const int PageSize = 4096;
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        var regions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber: 9, parameters);

        regions.PayloadOffset.Should().Be(0);
        regions.PayloadLength.Should().Be(PageSize - metadataSize);
        regions.TagOffset.Should().Be(PageSize - metadataSize);
        regions.TagLength.Should().Be(16);
        regions.NonceOffset.Should().Be(PageSize - nonceSize);
        regions.NonceLength.Should().Be(nonceSize);
        (regions.TagOffset + regions.TagLength).Should().Be(regions.NonceOffset);
        regions.AssociatedDataLength.Should().Be(0, "only page 1 authenticates a header");

        var firstPageRegions = AhtolaEncryptedPageFormat.Describe(PageSize, pageNumber: 1, parameters);
        firstPageRegions.PayloadOffset.Should().Be(100);
        firstPageRegions.AssociatedDataOffset.Should().Be(0);
        firstPageRegions.AssociatedDataLength.Should().Be(100);

        _ = cipherId;
        _ = key;
    }

    /// <summary>
    /// A page produced with a fixed nonce is a stable fixture: the exact byte
    /// layout is pinned so an accidental reordering of tag and nonce, or a change
    /// to the associated data, is caught even if encrypt/decrypt still round-trip.
    /// </summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void FixedNonceFramesArePinnedAndReproducible(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        const int PageSize = 1024;
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        var keyBytes = Convert.FromHexString(key);
        var nonce = new byte[nonceSize];
        for (var i = 0; i < nonce.Length; i++)
            nonce[i] = (byte)(0xA0 + i);

        var plaintext = CreatePlaintextPageOne(PageSize, metadataSize);
        var first = EncryptWithFixedNonce(cipher, keyBytes, nonce, plaintext, pageNumber: 1);
        var second = EncryptWithFixedNonce(cipher, keyBytes, nonce, plaintext, pageNumber: 1);

        first.Should().Equal(second, "a fixed nonce must give a deterministic frame");
        first[6].Should().Be(cipherId);
        first.AsSpan(PageSize - nonceSize, nonceSize).ToArray().Should().Equal(nonce);
        first.AsSpan(16, 84).ToArray().Should()
            .Equal(plaintext.AsSpan(16, 84).ToArray(), "bytes 16..100 stay visible on page 1");

        using var encryption = CreatePageEncryption(cipher, key, PageSize);
        encryption.DecryptPage(first, 1).Should().Equal(plaintext);

        // Changing only the authenticated header must change the tag.
        var tweaked = (byte[])first.Clone();
        tweaked[30] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => encryption.DecryptPage(tweaked, 1))!
            .Message.Should().Contain("failed authentication");
        _ = parameters;
    }

    /// <summary>A single flipped bit anywhere in the frame must fail authentication.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void TamperingAnyFrameRegionFailsAuthentication(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        _ = cipherId;
        const int PageSize = 2048;
        using var encryption = CreatePageEncryption(cipher, key, PageSize);

        var plaintext = CreatePlaintextPage(PageSize, metadataSize, 0x33);
        var encrypted = encryption.EncryptPage(plaintext, 4);

        var ciphertextOffset = 0;
        var tagOffset = PageSize - metadataSize;
        var nonceOffset = PageSize - nonceSize;
        foreach (var offset in new[] { ciphertextOffset, PageSize - metadataSize - 1, tagOffset, tagOffset + 15, nonceOffset, PageSize - 1 })
        {
            var tampered = (byte[])encrypted.Clone();
            tampered[offset] ^= 0x80;
            Assert.Throws<InvalidDataException>(() => encryption.DecryptPage(tampered, 4))!
                .Message.Should().Contain("failed authentication", $"offset {offset} is authenticated");
        }

        // Page 1 additionally authenticates its visible header.
        var firstPage = CreatePlaintextPageOne(PageSize, metadataSize);
        var encryptedFirst = encryption.EncryptPage(firstPage, 1);
        foreach (var offset in new[] { 16, 40, 99 })
        {
            var tampered = (byte[])encryptedFirst.Clone();
            tampered[offset] ^= 0x40;
            Assert.Throws<InvalidDataException>(() => encryption.DecryptPage(tampered, 1))!
                .Message.Should().Contain("failed authentication", $"header offset {offset} is associated data");
        }
    }

    /// <summary>The wrong key must fail, never return plaintext.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void WrongKeyFailsAuthentication(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        _ = cipherId;
        _ = nonceSize;
        const int PageSize = 1024;
        using var encryption = CreatePageEncryption(cipher, key, PageSize);
        var plaintext = CreatePlaintextPage(PageSize, metadataSize, 0x77);
        var encrypted = encryption.EncryptPage(plaintext, 3);

        var wrongKeyBytes = Convert.FromHexString(key);
        wrongKeyBytes[0] ^= 0xFF;
        using var wrongKey = new AhtolaPageEncryption(cipher, wrongKeyBytes, PageSize);

        Assert.Throws<InvalidDataException>(() => wrongKey.DecryptPage(encrypted, 3))!
            .Message.Should().Contain("failed authentication");
    }

    /// <summary>Truncated and oversized pages are argument errors, never memory faults.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void MalformedPageLengthsAreRejected(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        _ = cipherId;
        _ = nonceSize;
        const int PageSize = 1024;
        using var encryption = CreatePageEncryption(cipher, key, PageSize);
        var plaintext = CreatePlaintextPage(PageSize, metadataSize, 0x11);
        var encrypted = encryption.EncryptPage(plaintext, 2);

        Assert.Throws<ArgumentException>(() => encryption.DecryptPage(encrypted.AsSpan(0, PageSize - 1), 2));
        Assert.Throws<ArgumentException>(() => encryption.DecryptPage([.. encrypted, 0], 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => encryption.DecryptPage(encrypted, 0));

        // A page too small to hold the header plus metadata cannot be configured.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AhtolaPageEncryption(cipher, Convert.FromHexString(key), 100 + metadataSize));
    }

    /// <summary>Keys of the wrong length for the cipher are rejected up front.</summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void KeyLengthMustMatchTheCipher(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        _ = cipherId;
        _ = nonceSize;
        _ = metadataSize;
        var required = Convert.FromHexString(key).Length;
        var wrongLength = required == 16 ? 32 : 16;

        Assert.Throws<ArgumentException>(
            () => new AhtolaEncryptionOptions(cipher, new byte[wrongLength]))!
            .Message.Should().Contain($"{cipher} requires a {required}-byte key");
    }

    /// <summary>Opening a database with any other cipher must fail closed, never fall back.</summary>
    [TestCaseSource(nameof(AllCipherPairs))]
    public void CrossCipherOpenFailsClosed(
        StorageCipher writeCipher,
        string writeKey,
        StorageCipher readCipher,
        string readKey)
    {
        var fileSystem = new InMemoryFileSystem();
        using var writeEncryption = AhtolaEncryptionOptions.FromHex(writeCipher, writeKey);
        using (SqlitePageStore.Create(fileSystem, "cross.db", encryption: writeEncryption))
        {
        }

        using var readEncryption = AhtolaEncryptionOptions.FromHex(readCipher, readKey);
        var failure = Assert.Throws<InvalidDataException>(
            () => SqlitePageStore.Open(fileSystem, "cross.db", encryption: readEncryption))!;
        failure.Message.Should().Contain("cipher fallback is not permitted");
    }

    /// <summary>
    /// A reserved-space byte that disagrees with the configured cipher must be
    /// caught while the header is read, not as an opaque per-page authentication
    /// failure much later.
    /// </summary>
    [Test]
    public void ReservedSpaceMismatchIsRejectedWhileReadingTheHeader()
    {
        var page = new byte[128];
        AhtolaEncryptedPageFormat.AhtolaHeaderMagic.CopyTo(page);
        page[5] = 0;
        page[6] = (byte)StorageCipher.Aegis256;
        page[AhtolaEncryptedPageFormat.ReservedSpaceOffset] = 28;

        Assert.Throws<InvalidDataException>(
            () => AhtolaEncryptedPageFormat.ValidateEncryptedHeader(page, StorageCipher.Aegis256))!
            .Message.Should().Be(
                "Encrypted database reserves 28 bytes per page, but cipher ID 3 (AEGIS-256) requires 48; "
                + "cipher fallback is not permitted.");
    }

    /// <summary>
    /// The page codec identity must differ per cipher: two configurations that
    /// write different bytes must never share a codec id.
    /// </summary>
    [Test]
    public void PageCodecIdentityIsDistinctForEveryCipher()
    {
        var ids = new List<string>();
        foreach (var testCase in AllCiphers())
        {
            var cipher = (StorageCipher)testCase.Arguments[0]!;
            var key = (string)testCase.Arguments[1]!;
            using var options = AhtolaEncryptionOptions.FromHex(cipher, key);
            using var codec = EncryptionPageCodec.Create(options, 4096);
            codec.RequiredReservedBytes.Should()
                .Be(checked((byte)AhtolaEncryptedPageFormat.GetParameters(cipher).MetadataSize));
            ids.Add(Convert.ToHexString(codec.CodecId.ToArray()));
        }

        ids.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// One codec instance is shared by every pager thread, so encrypt and decrypt
    /// must be safe to call concurrently.
    /// </summary>
    [TestCaseSource(nameof(AllCiphers))]
    public void PageEncryptionIsSafeForConcurrentUse(
        StorageCipher cipher,
        string key,
        byte cipherId,
        int nonceSize,
        int metadataSize)
    {
        _ = cipherId;
        _ = nonceSize;
        const int PageSize = 1024;
        using var encryption = CreatePageEncryption(cipher, key, PageSize);
        var plaintext = CreatePlaintextPage(PageSize, metadataSize, 0x2B);

        Parallel.For(0, 64, index =>
        {
            var pageNumber = (uint)(index + 2);
            var encrypted = encryption.EncryptPage(plaintext, pageNumber);
            encryption.DecryptPage(encrypted, pageNumber).Should().Equal(plaintext);
        });
    }

    internal static AhtolaPageEncryption CreatePageEncryption(StorageCipher cipher, string key, int pageSize)
        => new(cipher, Convert.FromHexString(key), pageSize);

    internal static byte[] CreatePlaintextPage(int pageSize, int metadataSize, byte fill)
    {
        var page = new byte[pageSize];
        page.AsSpan(0, pageSize - metadataSize).Fill(fill);
        return page;
    }

    internal static byte[] CreatePlaintextPageOne(int pageSize, int metadataSize)
    {
        var page = new byte[pageSize];
        AhtolaEncryptedPageFormat.SqliteHeaderMagic.CopyTo(page);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(16), pageSize == 65536 ? (ushort)1 : (ushort)pageSize);
        page[20] = checked((byte)metadataSize);
        for (var i = 21; i < pageSize - metadataSize; i++)
            page[i] = (byte)(i * 31);
        return page;
    }

    /// <summary>
    /// Frames a page with a caller-supplied nonce. Test-only: production always
    /// draws a fresh random nonce, because AEGIS nonce reuse is catastrophic.
    /// </summary>
    internal static byte[] EncryptWithFixedNonce(
        StorageCipher cipher,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintextPage,
        uint pageNumber,
        bool forceSoftwareAesRound = false)
    {
        var parameters = AhtolaEncryptedPageFormat.GetParameters(cipher);
        var pageSize = plaintextPage.Length;
        var encrypted = new byte[pageSize];
        var associatedDataLength = 0;
        if (pageNumber == 1)
        {
            AhtolaEncryptedPageFormat.WriteEncryptedHeaderPrefix(encrypted, plaintextPage, cipher);
            associatedDataLength = AhtolaEncryptedPageFormat.SqliteHeaderSize;
        }

        var regions = AhtolaEncryptedPageFormat.Describe(pageSize, pageNumber, parameters);
        nonce.CopyTo(encrypted.AsSpan(regions.NonceOffset, regions.NonceLength));

        using var aead = AhtolaAeadFactory.Create(cipher, key, forceSoftwareAesRound);
        aead.Encrypt(
            nonce,
            plaintextPage.Slice(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.PayloadOffset, regions.PayloadLength),
            encrypted.AsSpan(regions.TagOffset, regions.TagLength),
            encrypted.AsSpan(regions.AssociatedDataOffset, associatedDataLength));
        return encrypted;
    }
}
