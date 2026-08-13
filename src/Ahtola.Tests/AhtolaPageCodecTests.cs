using AwesomeAssertions;
using Ahtola.Core.Storage;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

/// <summary>
/// Managed page-codec surface (Turso PR #8183 / #8095): external IPageCodec
/// round-trips, zero-id rejection, and mutual exclusion with built-in encryption.
/// </summary>
public class AhtolaPageCodecTests
{
    private static readonly byte[] Aes256Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    [Test]
    public void PageStoreRoundTripsWithXorPageCodec()
    {
        var fileSystem = new InMemoryFileSystem();
        var codec = new XorPageCodec(0x5A);
        var page = CreatePlainPage(SqlitePageSize.Default, 0xA1);

        using (var store = SqlitePageStore.Create(fileSystem, "codec.db", pageCodec: codec))
        {
            store.Header.ReservedSpace.Should().Be(0);
            store.WritePage(2, page);
        }

        using (var raw = fileSystem.OpenFile("codec.db", FileOpenMode.OpenExisting, readOnly: true))
        {
            var encodedSecond = new byte[SqlitePageSize.Default];
            raw.Read(SqlitePageSize.Default, encodedSecond).Should().Be(encodedSecond.Length);
            encodedSecond.Should().NotEqual(page);
            encodedSecond[0].Should().Be((byte)(page[0] ^ 0x5A));
        }

        using var reopened = SqlitePageStore.Open(fileSystem, "codec.db", pageCodec: codec);
        reopened.ReadPage(2).Should().Equal(page);
    }

    [Test]
    public void PagerRoundTripsWalFramesWithXorPageCodec()
    {
        var fileSystem = new InMemoryFileSystem();
        var codec = new XorPageCodec(0x3C);
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 1, salt2: 2);
        var page = CreatePlainPage(SqlitePageSize.Default, 0xB2);

        using (var pager = SqlitePager.Create(
                   fileSystem,
                   "codec.db",
                   "codec.db-wal",
                   walHeader,
                   pageCodec: codec))
        {
            using var transaction = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
            transaction.WritePage(2, page);
            transaction.Commit();
        }

        using (var raw = fileSystem.OpenFile("codec.db-wal", FileOpenMode.OpenExisting, readOnly: true))
        {
            var encodedPage = new byte[SqlitePageSize.Default];
            raw.Read(SqliteWalHeader.Size + SqliteWalFrameHeader.Size, encodedPage)
                .Should().Be(encodedPage.Length);
            encodedPage.Should().NotEqual(page);
        }

        using var reopened = SqlitePager.Open(
            fileSystem,
            "codec.db",
            "codec.db-wal",
            pageCodec: codec);
        reopened.ReadCommittedPage(2).Should().Equal(page);
    }

    [Test]
    public void FileSystemCarrierAppliesCodecOnPagerOpen()
    {
        var inner = new InMemoryFileSystem();
        var codec = new XorPageCodec(0x11);
        using var codecFs = new AhtolaPageCodecFileSystem(inner, codec);
        var page = CreatePlainPage(SqlitePageSize.Default, 0xC3);

        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 3, salt2: 4);
                using (var pager = SqlitePager.Create(codecFs, "fs.db", "fs.db-wal", walHeader))
                {
                    using var tx = pager.BeginTransaction(targetDatabaseSizeInPages: 2);
                    tx.WritePage(2, page);
                    tx.Commit();
                }

        using var reopened = SqlitePager.Open(codecFs, "fs.db", "fs.db-wal");
        reopened.ReadCommittedPage(2).Should().Equal(page);
    }

    [Test]
    public void RejectsZeroCodecId()
    {
        var act = () => new XorPageCodec(0x00, forceZeroId: true);
        act.Should().Throw<ArgumentException>().WithMessage("*non-zero*");

        var zeroId = new PageCodecId(new byte[16]);
        var validate = () => PageCodecId.ValidateNonZero(zeroId);
        validate.Should().Throw<ArgumentException>().WithMessage("*non-zero*");
    }

    [Test]
    public void RejectsCombiningEncryptionAndExternalCodec()
    {
        var fileSystem = new InMemoryFileSystem();
        using var encryption = new AhtolaEncryptionOptions(StorageCipher.Aes256Gcm, Aes256Key);
        var codec = new XorPageCodec(0x42);

        var create = () => SqlitePageStore.Create(
            fileSystem,
            "both.db",
            encryption: encryption,
            pageCodec: codec);
        create.Should().Throw<ArgumentException>().WithMessage("*cannot be combined*");

        var nested = () => new AhtolaPageCodecFileSystem(
            new AhtolaEncryptionFileSystem(fileSystem, encryption),
            codec);
        nested.Should().Throw<ArgumentException>().WithMessage("*cannot be combined*");
    }

    [Test]
    public void ConnectionOpensDatabaseWithPageCodec()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-codec-{Guid.NewGuid():N}.db");
        try
        {
            var codec = new XorPageCodec(0x77);
            using (var connection = new AhtolaConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=False"))
            {
                connection.PageCodec = codec;
                connection.Open();
                                using (var create = connection.CreateCommand())
                                {
                                    create.CommandText = "CREATE TABLE t(x INTEGER);";
                                    create.ExecuteNonQuery();
                                }

                                using (var insert = connection.CreateCommand())
                                {
                                    insert.CommandText = "INSERT INTO t VALUES (42);";
                                    insert.ExecuteNonQuery();
                                }
                            }

            using (var connection = new AhtolaConnection(
                       $"Data Source={path};Local Provider=Managed;Pooling=False"))
            {
                connection.PageCodec = codec;
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT x FROM t;";
                Convert.ToInt64(command.ExecuteScalar()).Should().Be(42);
            }

            // Raw file should not contain a plaintext SQLite header magic at offset 0
            // when the first page body is XOR-transformed (magic is still visible for
            // bootstrap-compatible codecs — Xor transforms all bytes including magic).
            var raw = File.ReadAllBytes(path);
            raw.AsSpan(0, 16).ToArray().Should().NotEqual("SQLite format 3\0"u8.ToArray());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            var wal = path + "-wal";
            var shm = path + "-shm";
            if (File.Exists(wal))
                File.Delete(wal);
            if (File.Exists(shm))
                File.Delete(shm);
        }
    }

    [Test]
    public void ConnectionRejectsEncryptionPlusPageCodec()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahtola-codec-reject-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new AhtolaConnection(
                $"Data Source={path};Local Provider=Managed;Pooling=False;" +
                "Encryption Cipher=aes256gcm;" +
                $"Encryption Key={Convert.ToHexString(Aes256Key)}");
            connection.PageCodec = new XorPageCodec(0x01);
            var open = () => connection.Open();
            open.Should().Throw<InvalidOperationException>().WithMessage("*cannot be combined*");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static byte[] CreatePlainPage(int pageSize, byte fill)
    {
        var page = new byte[pageSize];
        page.AsSpan().Fill(fill);
        return page;
    }

    /// <summary>Test-only XOR transform; reserved bytes stay 0 (header layout unchanged).</summary>
    private sealed class XorPageCodec : IPageCodec
    {
        private readonly byte _mask;

        public XorPageCodec(byte mask, bool forceZeroId = false)
        {
            _mask = mask;
            Span<byte> id = stackalloc byte[16];
            if (!forceZeroId)
            {
                "ahtola-xor-test-"u8.CopyTo(id);
                id[15] = mask;
            }

            var codecId = new PageCodecId(id);
            PageCodecId.ValidateNonZero(codecId);
            CodecId = codecId;
        }

        public PageCodecId CodecId { get; }

        public byte RequiredReservedBytes => 0;

                public PageCodecHeaderInfo BootstrapPageInfo(ReadOnlySpan<byte> rawPage1Prefix)
                {
                    // Full-page XOR hides the SQLite layout fields; decode the bootstrap
                    // prefix first so open can recover page size / reserved space.
                    Span<byte> decoded = stackalloc byte[rawPage1Prefix.Length];
                    Xor(rawPage1Prefix, decoded);
                    return PageCodecHeaderInfo.FromVisibleSqliteHeader(decoded);
                }

                public void EncodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
                    => Xor(input, output);

                public void DecodePage(PageCodecContext context, ReadOnlySpan<byte> input, Span<byte> output)
                    => Xor(input, output);

        private void Xor(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (input.Length != output.Length)
                throw new ArgumentException("XOR codec requires equal lengths.");
            for (var i = 0; i < input.Length; i++)
                output[i] = (byte)(input[i] ^ _mask);
        }
    }
}
