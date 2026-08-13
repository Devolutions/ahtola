using System.Security.Cryptography;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

public class AhtolaEncryptedStorageTests
{
    private static readonly byte[] Aes256Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    [Test]
    public void PageStoreEncryptsPagesAndReopensOnlyWithTheCorrectKey()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(StorageCipher.Aes256Gcm, Aes256Key);
        var page = CreatePlainPage(SqlitePageSize.Default, 0xA1);

        using (var store = SqlitePageStore.Create(fileSystem, "encrypted.db", encryption: encryption))
        {
            store.Header.ReservedSpace.Should().Be(28);
            store.WritePage(2, page);
        }

        using (var raw = fileSystem.OpenFile("encrypted.db", FileOpenMode.OpenExisting, readOnly: true))
        {
            var encryptedFirstPage = new byte[SqlitePageSize.Default];
            raw.Read(0, encryptedFirstPage).Should().Be(encryptedFirstPage.Length);
            encryptedFirstPage.AsSpan(0, 7).ToArray().Should().Equal("AHTLA"u8.ToArray().Append((byte)0).Append((byte)2));

            var encryptedSecondPage = new byte[SqlitePageSize.Default];
            raw.Read(SqlitePageSize.Default, encryptedSecondPage).Should().Be(encryptedSecondPage.Length);
            encryptedSecondPage.Should().NotEqual(page);
        }

        using (var reopened = SqlitePageStore.Open(fileSystem, "encrypted.db", encryption: encryption))
        {
            reopened.Header.ReservedSpace.Should().Be(28);
            reopened.ReadPage(2).Should().Equal(page);
        }

        var missingKey = Assert.Throws<InvalidDataException>(() => SqlitePageStore.Open(fileSystem, "encrypted.db"));
        missingKey!.Message.Should().Contain("encrypted");

        using var wrongKey = new AhtolaEncryptionOptions(
            StorageCipher.Aes256Gcm,
            Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"));
        var wrongKeyFailure = Assert.Throws<InvalidDataException>(
            () => SqlitePageStore.Open(fileSystem, "encrypted.db", encryption: wrongKey));
        wrongKeyFailure!.Message.Should().Contain("failed authentication");
    }

    [Test]
    public void PageStoreRejectsPlaintextAndTamperedEncryptedFiles()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(StorageCipher.Aes256Gcm, Aes256Key);

        using (SqlitePageStore.Create(fileSystem, "plaintext.db"))
        {
        }

        var plaintextFailure = Assert.Throws<InvalidDataException>(
            () => SqlitePageStore.Open(fileSystem, "plaintext.db", encryption: encryption));
        plaintextFailure!.Message.Should().Contain("Plaintext fallback");

        using (var unsupported = fileSystem.OpenFile("unsupported.db", FileOpenMode.CreateNew))
        {
            var header = CreateFirstPage(SqlitePageSize.Default);
            "AHTLA"u8.CopyTo(header);
            header[5] = 0;
            header[6] = 3;
            unsupported.Write(0, header);
        }

        var unsupportedFailure = Assert.Throws<InvalidDataException>(
            () => SqlitePageStore.Open(fileSystem, "unsupported.db", encryption: encryption));
        unsupportedFailure!.Message.Should().Contain("will not infer or fall back to another cipher");

        using (SqlitePageStore.Create(fileSystem, "tampered.db", encryption: encryption))
        {
        }

        using (var raw = fileSystem.OpenFile("tampered.db", FileOpenMode.OpenExisting))
        {
            var original = new byte[1];
            raw.Read(50, original).Should().Be(1);
            raw.Write(50, [(byte)(original[0] ^ 0x80)]);
        }

        var tamperFailure = Assert.Throws<InvalidDataException>(
            () => SqlitePageStore.Open(fileSystem, "tampered.db", encryption: encryption));
        tamperFailure!.Message.Should().Contain("failed authentication");
    }

    [Test]
    public void OpensDeterministicRustAesGcmPageFixture()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(StorageCipher.Aes256Gcm, Aes256Key);
        var plaintext = CreateFirstPage(SqlitePageSize.Default);
        var nonce = Convert.FromHexString("101112131415161718191A1B");
        var encrypted = CreateRustAes256PageFixture(plaintext, Aes256Key, nonce);

        using (var file = fileSystem.OpenFile("fixture.db", FileOpenMode.CreateNew))
        {
            file.Write(0, encrypted);
            file.FlushToDisk();
        }

        using var store = SqlitePageStore.Open(fileSystem, "fixture.db", encryption: encryption);
        store.ReadPage(1).Should().Equal(plaintext);
    }

    [Test]
    public void PagerEncryptsWalFramesAndAuthenticatesTheirPageImages()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(StorageCipher.Aes128Gcm, Aes256Key.AsSpan()[..16]);
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 1, salt2: 2);
        var page = CreatePlainPage(SqlitePageSize.Default, 0xB2);

        using (var pager = SqlitePager.Create(
                   fileSystem,
                   "encrypted.db",
                   "encrypted.db-wal",
                   walHeader,
                   encryption: encryption))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, page);
            transaction.Commit();
        }

        using (var raw = fileSystem.OpenFile("encrypted.db-wal", FileOpenMode.OpenExisting, readOnly: true))
        {
            var encryptedPage = new byte[SqlitePageSize.Default];
            raw.Read(SqliteWalHeader.Size + SqliteWalFrameHeader.Size, encryptedPage).Should().Be(encryptedPage.Length);
            encryptedPage.Should().NotEqual(page);
        }

        using (var reopened = SqlitePager.Open(
                   fileSystem,
                   "encrypted.db",
                   "encrypted.db-wal",
                   encryption: encryption))
        {
            reopened.ReadCommittedPage(2).Should().Equal(page);
        }

        using var wrongKey = new AhtolaEncryptionOptions(
            StorageCipher.Aes128Gcm,
            Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"));
        using var wrongKeyWal = SqliteWalFile.Open(fileSystem, "encrypted.db-wal", encryption: wrongKey);
        var wrongKeyFailure = Assert.Throws<InvalidDataException>(
            () => wrongKeyWal.ReadFrame(1));
        wrongKeyFailure!.Message.Should().Contain("failed authentication");
    }

    private static byte[] CreatePlainPage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        page.AsSpan(0, pageSize - AhtolaPageEncryptionMetadataSize).Fill(fill);
        return page;
    }

    private static byte[] CreateFirstPage(int pageSize)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with
        {
            ReservedSpace = AhtolaPageEncryptionMetadataSize,
            ChangeCounter = 1,
            DatabaseSizeInPages = 1,
            VersionValidFor = 1,
        };
        var page = new byte[pageSize];
        header.WriteTo(page);
        SqliteBtreePageHeader
            .CreateEmpty(SqliteBtreePageType.TableLeaf, pageSize, isFirstPage: true, header.UsableSpace)
            .WriteTo(page);
        return page;
    }

    private static byte[] CreateRustAes256PageFixture(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce)
    {
        const int sqliteHeaderSize = 100;
        const int metadataSize = AhtolaPageEncryptionMetadataSize;
        const int tagSize = 16;
        var encrypted = new byte[plaintext.Length];
        "AHTLA"u8.CopyTo(encrypted);
        encrypted[5] = 0;
        encrypted[6] = (byte)StorageCipher.Aes256Gcm;
        plaintext[16..sqliteHeaderSize].CopyTo(encrypted.AsSpan(16));

        var payloadLength = plaintext.Length - sqliteHeaderSize - metadataSize;
        using var cipher = new AesGcm(key, tagSize);
        cipher.Encrypt(
            nonce,
            plaintext.Slice(sqliteHeaderSize, payloadLength),
            encrypted.AsSpan(sqliteHeaderSize, payloadLength),
            encrypted.AsSpan(plaintext.Length - metadataSize, tagSize),
            encrypted.AsSpan(0, sqliteHeaderSize));
        nonce.CopyTo(encrypted.AsSpan(plaintext.Length - nonce.Length));
        return encrypted;
    }

    private const int AhtolaPageEncryptionMetadataSize = 28;
}
