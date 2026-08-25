using System.Buffers.Binary;
using AwesomeAssertions;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

public sealed class ManagedEncryptionFormatInteropTests
{
    private const string Aes128Key = "000102030405060708090A0B0C0D0E0F";
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [TestCase(StorageCipher.Aes128Gcm, Aes128Key, 1, 28)]
    [TestCase(StorageCipher.Aes256Gcm, Aes256Key, 2, 28)]
    [TestCase(StorageCipher.Aegis256, Aes256Key, 3, 48)]
    [TestCase(StorageCipher.Aegis256X2, Aes256Key, 4, 48)]
    [TestCase(StorageCipher.Aegis256X4, Aes256Key, 5, 48)]
    [TestCase(StorageCipher.Aegis128L, Aes128Key, 6, 32)]
    [TestCase(StorageCipher.Aegis128X2, Aes128Key, 7, 32)]
    [TestCase(StorageCipher.Aegis128X4, Aes128Key, 8, 32)]
    public void ManagedWriterUsesAhtolaFormatVersionZeroAndExactTursoCipherIds(
        StorageCipher cipher,
        string key,
        byte cipherId,
        byte reservedBytes)
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(cipher, key);
        using (SqlitePageStore.Create(fileSystem, "format.db", encryption: encryption))
        {
        }

        var firstPage = ReadFile(fileSystem, "format.db");
        firstPage.AsSpan(0, 5).ToArray().Should().Equal("AHTLA"u8.ToArray());
        firstPage[5].Should().Be(0);
        firstPage[6].Should().Be(cipherId);
        firstPage.AsSpan(7, 9).ToArray().Should().OnlyContain(value => value == 0);
        firstPage[20].Should().Be(reservedBytes, "the frame reserves a 16-byte tag plus this cipher's nonce");

        using var reopened = SqlitePageStore.Open(fileSystem, "format.db", encryption: encryption);
        reopened.Header.ReservedSpace.Should().Be(reservedBytes);
    }

    [Test]
    public void ManagedReaderRejectsUnsupportedFormatVersionWithoutFallback()
    {
        var (fileSystem, encryption) = CreateEncryptedStore();
        using (encryption)
        {
            MutateByte(fileSystem, "format.db", 5, 1);

            Assert.Throws<InvalidDataException>(
                () => SqlitePageStore.Open(fileSystem, "format.db", encryption: encryption))!
                .Message.Should().Be(
                    "Unsupported Ahtola encrypted database format version 1; managed storage supports only "
                    + "format version 0 and will not infer or fall back to another format.");
        }
    }

    [TestCase(0, "none")]
    [TestCase(9, "unknown")]
    [TestCase(255, "unknown")]
    public void ManagedReaderRejectsCipherIdsOutsideFormatVersionZeroWithoutInference(byte cipherId, string cipherName)
    {
        var (fileSystem, encryption) = CreateEncryptedStore();
        using (encryption)
        {
            MutateByte(fileSystem, "format.db", 6, cipherId);

            Assert.Throws<InvalidDataException>(
                () => SqlitePageStore.Open(fileSystem, "format.db", encryption: encryption))!
                .Message.Should().Be(
                    $"Encrypted database uses Ahtola cipher ID {cipherId} ({cipherName}); managed storage supports "
                    + "only cipher IDs 1 through 8 for format version 0 "
                    + "and will not infer or fall back to another cipher.");
        }
    }

    [TestCase(3, "AEGIS-256")]
    [TestCase(4, "AEGIS-256X2")]
    [TestCase(5, "AEGIS-256X4")]
    [TestCase(6, "AEGIS-128L")]
    [TestCase(7, "AEGIS-128X2")]
    [TestCase(8, "AEGIS-128X4")]
    public void ManagedReaderRejectsEveryCipherIdItWasNotConfiguredForWithoutInference(byte cipherId, string cipherName)
    {
        var (fileSystem, encryption) = CreateEncryptedStore();
        using (encryption)
        {
            MutateByte(fileSystem, "format.db", 6, cipherId);

            Assert.Throws<InvalidDataException>(
                () => SqlitePageStore.Open(fileSystem, "format.db", encryption: encryption))!
                .Message.Should().Be(
                    $"Encrypted database uses Ahtola cipher ID {cipherId} ({cipherName}), but the supplied options "
                    + "specify cipher ID 2 (AES-256-GCM); cipher fallback is not permitted.");
        }
    }

    [Test]
    public void ManagedReaderRejectsConfiguredCipherMismatchBeforeTryingAnotherCipher()
    {
        var fileSystem = new InMemoryFileSystem();
        using var aes256 = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Aes256Key);
        using (SqlitePageStore.Create(fileSystem, "format.db", encryption: aes256))
        {
        }

        using var aes128 = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes128Gcm, Aes128Key);
        Assert.Throws<InvalidDataException>(
            () => SqlitePageStore.Open(fileSystem, "format.db", encryption: aes128))!
            .Message.Should().Be(
                "Encrypted database uses Ahtola cipher ID 2 (AES-256-GCM), but the supplied options specify "
                + "cipher ID 1 (AES-128-GCM); cipher fallback is not permitted.");
    }

    [TestCase(StorageCipher.Aes128Gcm, Aes256Key, 16)]
    [TestCase(StorageCipher.Aes256Gcm, Aes128Key, 32)]
    public void ManagedOptionsRejectKeyLengthThatDoesNotMatchCipher(
        StorageCipher cipher,
        string key,
        int requiredKeyLength)
    {
        Assert.Throws<ArgumentException>(
            () => AhtolaEncryptionOptions.FromHex(cipher, key))!
            .Message.Should().Contain(
                $"{cipher} requires a {requiredKeyLength}-byte key");
    }

    [Test]
    public void ManagedOptionsRejectNonHexadecimalKey()
    {
        Assert.Throws<ArgumentException>(
            () => AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, "not-hex"))!
            .Message.Should().Contain("Encryption keys must be hexadecimal");
    }

    [Test]
    public void ManagedReaderRejectsAuthenticatedHeaderReservedByteTampering()
    {
        var (fileSystem, encryption) = CreateEncryptedStore();
        using (encryption)
        {
            MutateByte(fileSystem, "format.db", 7, 1);

            Assert.Throws<InvalidDataException>(
                () => SqlitePageStore.Open(fileSystem, "format.db", encryption: encryption))!
                .Message.Should().Be("Ahtola encrypted database header has non-zero reserved bytes.");
        }
    }

    [Test]
    public void EncryptedWalRecoveryTruncatesOnlyPartialTailAndReopensCommittedData()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Aes256Key);
        var committedPage = CreatePlainPage(0x5A);
        long committedWalLength;

        using (var pager = CreatePager(fileSystem, encryption))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, committedPage);
            transaction.Commit();
            committedWalLength = SqliteWalHeader.Size + SqliteWalFrameHeader.Size + SqlitePageSize.Default;
        }

        using (var wal = fileSystem.OpenFile("format.db-wal", FileOpenMode.OpenExisting))
        {
            wal.Write(wal.Length, [0xA1, 0xA2, 0xA3]);
            wal.FlushToDisk();
            wal.Length.Should().Be(committedWalLength + 3);
        }

        using (var reopened = SqlitePager.Open(
                   fileSystem,
                   "format.db",
                   "format.db-wal",
                   encryption: encryption))
        {
            reopened.ReadCommittedPage(2).Should().Equal(committedPage);
            reopened.RecoveryInfo.StopReason.Should().Be(SqliteWalRecoveryStopReason.PartialFrame);
        }

        using var repairedWal = fileSystem.OpenFile("format.db-wal", FileOpenMode.OpenExisting, readOnly: true);
        repairedWal.Length.Should().Be(committedWalLength);
    }

    [Test]
    public void AuthenticatedWalTamperingFailsRecoveryWithoutChangingEvidence()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Aes256Key);
        using (var pager = CreatePager(fileSystem, encryption))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, CreatePlainPage(0x6B));
            transaction.Commit();
        }

        var tamperedWal = ReadFile(fileSystem, "format.db-wal");
        var header = SqliteWalHeader.Parse(tamperedWal.AsSpan(0, SqliteWalHeader.Size));
        var frameOffset = SqliteWalHeader.Size;
        var pageOffset = frameOffset + SqliteWalFrameHeader.Size;
        tamperedWal[pageOffset + 31] ^= 0x80;
        var framePrefixChecksum = SqliteWalChecksum.Calculate(
            tamperedWal.AsSpan(frameOffset, 8),
            header.ChecksumByteOrder,
            header.Checksum1,
            header.Checksum2);
        var frameChecksum = SqliteWalChecksum.Calculate(
            tamperedWal.AsSpan(pageOffset, header.PageSize),
            header.ChecksumByteOrder,
            framePrefixChecksum.First,
            framePrefixChecksum.Second);
        BinaryPrimitives.WriteUInt32BigEndian(tamperedWal.AsSpan(frameOffset + 16, 4), frameChecksum.First);
        BinaryPrimitives.WriteUInt32BigEndian(tamperedWal.AsSpan(frameOffset + 20, 4), frameChecksum.Second);
        tamperedWal = [.. tamperedWal, 0xA1, 0xA2, 0xA3];
        WriteFile(fileSystem, "format.db-wal", tamperedWal);

        Assert.Throws<InvalidDataException>(
            () => SqlitePager.Open(
                fileSystem,
                "format.db",
                "format.db-wal",
                encryption: encryption))!
            .Message.Should().Contain("failed authentication");
        ReadFile(fileSystem, "format.db-wal").Should().Equal(tamperedWal);
    }

    [Test]
    public void ManagedBackupReencryptsSnapshotWithDestinationCipherAndKey()
    {
        var sourcePath = CreateDatabasePath("source");
        var destinationPath = CreateDatabasePath("destination");
        var sourceConnectionString = CreateConnectionString(sourcePath, "AES256GCM", Aes256Key);
        var destinationConnectionString = CreateConnectionString(destinationPath, "AES128GCM", Aes128Key);
        try
        {
            using (var source = new SqliteConnection(sourceConnectionString))
            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                source.Open();
                source.ExecuteNonQuery("CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('backup');");
                destination.Open();

                source.BackupDatabase(destination);
                destination.ExecuteScalar<string>("SELECT value FROM data;").Should().Be("backup");
            }

            File.ReadAllBytes(sourcePath)[6].Should().Be(2);
            File.ReadAllBytes(destinationPath)[6].Should().Be(1);

            using (var reopened = new SqliteConnection(destinationConnectionString))
            {
                reopened.Open();
                reopened.ExecuteScalar<string>("SELECT value FROM data;").Should().Be("backup");
            }

            using var wrongCipher = new SqliteConnection(
                CreateConnectionString(destinationPath, "AES256GCM", Aes256Key));
            Assert.Throws<InvalidDataException>(() => wrongCipher.Open())!
                .Message.Should().Contain("cipher fallback is not permitted");
        }
        finally
        {
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
        }
    }

    [TestCase("AEGIS256", Aes256Key)]
    [TestCase("AEGIS256X2", Aes256Key)]
    [TestCase("AEGIS256X4", Aes256Key)]
    [TestCase("aegis-256x4", Aes256Key)]
    [TestCase("AEGIS128L", Aes128Key)]
    [TestCase("AEGIS128X2", Aes128Key)]
    [TestCase("AEGIS128X4", Aes128Key)]
    [TestCase("aegis_128x4", Aes128Key)]
    public void ManagedConnectionRoundTripsEveryFormatVersionZeroCipherName(string cipher, string key)
    {
        var path = CreateDatabasePath("cipher-names");
        try
        {
            var connectionString = CreateConnectionString(path, cipher, key);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                connection.ExecuteNonQuery("CREATE TABLE data(value TEXT); INSERT INTO data VALUES ('aegis');");
            }

            using (var reopened = new SqliteConnection(connectionString))
            {
                reopened.Open();
                reopened.ExecuteScalar<string>("SELECT value FROM data;").Should().Be("aegis");
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase("AEGIS256", Aes256Key)]
    [TestCase("AEGIS128L", Aes128Key)]
    public void AhtolaManagedConnectionRoundTripsEveryFormatVersionZeroCipherName(string cipher, string key)
    {
        var path = CreateDatabasePath("ahtola-cipher-names");
        try
        {
            var connectionString = CreateConnectionString(path, cipher, key);
            using (var connection = new global::Ahtola.AhtolaConnection(connectionString))
            {
                connection.Open();
                using (var create = connection.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE data(value TEXT);";
                    create.ExecuteNonQuery();
                }

                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO data VALUES ('aegis');";
                insert.ExecuteNonQuery();
            }

            using (var reopened = new global::Ahtola.AhtolaConnection(connectionString))
            {
                reopened.Open();
                using var command = reopened.CreateCommand();
                command.CommandText = "SELECT value FROM data;";
                command.ExecuteScalar().Should().Be("aegis");
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [TestCase("chacha20poly1305")]
    [TestCase("aes192gcm")]
    public void ManagedConnectionRejectsCipherNamesWithNoOnDiskCipherId(string cipher)
    {
        using var connection = new SqliteConnection(
            $"Data Source=test.db;Local Provider=Managed;Encryption Cipher={cipher};Encryption Key={Aes256Key}");

        Assert.Throws<NotSupportedException>(() => connection.Open())!
            .Message.Should().Be(
                "Local Provider=Managed supports only Ahtola encrypted format version 0 cipher IDs 1 through 8 "
                + "(AES128GCM, AES256GCM, AEGIS256, AEGIS256X2, AEGIS256X4, AEGIS128L, AEGIS128X2, AEGIS128X4); "
                + "cipher fallback is not permitted.");
    }

    private static (InMemoryFileSystem FileSystem, AhtolaEncryptionOptions Encryption) CreateEncryptedStore()
    {
        var fileSystem = new InMemoryFileSystem();
        var encryption = AhtolaEncryptionOptions.FromHex(StorageCipher.Aes256Gcm, Aes256Key);
        using (SqlitePageStore.Create(fileSystem, "format.db", encryption: encryption))
        {
        }

        return (fileSystem, encryption);
    }

    private static SqlitePager CreatePager(InMemoryFileSystem fileSystem, AhtolaEncryptionOptions encryption)
        => SqlitePager.Create(
            fileSystem,
            "format.db",
            "format.db-wal",
            SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 0x1122_3344, salt2: 0x5566_7788),
            encryption: encryption);

    private static byte[] CreatePlainPage(byte fill)
    {
        var page = new byte[SqlitePageSize.Default];
        page.AsSpan(0, page.Length - 28).Fill(fill);
        return page;
    }

    private static byte[] ReadFile(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting, readOnly: true);
        var bytes = new byte[checked((int)file.Length)];
        file.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }

    private static void WriteFile(IFileSystem fileSystem, string path, ReadOnlySpan<byte> bytes)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        file.Write(0, bytes);
        file.SetLength(bytes.Length);
        file.FlushToDisk();
    }

    private static void MutateByte(IFileSystem fileSystem, string path, long offset, byte value)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        file.Write(offset, [value]);
        file.FlushToDisk();
    }

    private static string CreateConnectionString(string path, string cipher, string key)
        => $"Data Source={path};Local Provider=Managed;Encryption Cipher={cipher};Encryption Key={key}";

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-encryption-format-interop-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
