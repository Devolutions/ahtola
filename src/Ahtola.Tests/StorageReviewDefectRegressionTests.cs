using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class StorageReviewDefectRegressionTests
{
    private static readonly byte[] Aes256Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    [Test]
    public void StorageReview_TableLeafOverflowShrinkIsRejectedBeforeAllocationAndKeepsTheOriginalChainReopenable()
    {
        var fileSystem = new InMemoryFileSystem();
        var databaseHeader = SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum };
        var largePayload = Enumerable.Range(0, 1_300).Select(value => unchecked((byte)value)).ToArray();

        using (var store = SqlitePageStore.Create(fileSystem, "storage-review-overflow.db", databaseHeader))
        {
            var createWriter = new SqliteTableLeafMutationWriter(
                store,
                new SqliteAppendOnlyPageAllocator(store));
            var created = createWriter.CreatePage([new SqliteTableLeafCellInput(1, largePayload)]);
            created.OverflowPages.Should().NotBeEmpty();
            created.ApplyTo(store);

            var allocator = new SqliteAppendOnlyPageAllocator(store);
            var rewriteWriter = new SqliteTableLeafMutationWriter(store, allocator);
            var originalPageCount = store.PageCount;

            Assert.Throws<NotSupportedException>(() => rewriteWriter.RewritePage(
                created.TableLeafPageNumber,
                [new SqliteTableLeafCellInput(1, [0x01])]))!
                .Message
                .Should()
                .Contain("freelist reclamation");

            allocator.NextPageNumber.Should().Be(originalPageCount + 1);
            store.PageCount.Should().Be(originalPageCount);
            var originalCell = SqliteTableLeafPageView.Parse(
                store.ReadPage(created.TableLeafPageNumber),
                store.Header.UsableSpace).Cells.Single().Cell;
            new SqliteOverflowChainReader(store).ReadPayload(originalCell).Should().Equal(largePayload);
        }

        using var reopened = SqlitePageStore.Open(fileSystem, "storage-review-overflow.db");
        var reopenedCell = SqliteTableLeafPageView.Parse(
            reopened.ReadPage(2),
            reopened.Header.UsableSpace).Cells.Single().Cell;
        new SqliteOverflowChainReader(reopened).ReadPayload(reopenedCell).Should().Equal(largePayload);
    }

    [Test]
    public void StorageReview_OverflowShrinkFreelistReuseStaysIntegrityCheckedAndReopenable()
    {
        var databasePath = CreatePhysicalDatabasePath();
        var retiredPayload = "storage-review-retired-" + new string('q', 12_000);
        var replacementPayload = "storage-review-replacement-" + new string('r', 64);
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(databasePath))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, payload TEXT);");
                Execute(connection, $"INSERT INTO entries VALUES (1, '{retiredPayload}');");
                Execute(connection, "UPDATE entries SET payload = 'small-committed' WHERE id = 1;");
            }

            uint reusableFreelistLeaf;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
                freelist.LeafPageNumbers.Should().NotBeEmpty();
                reusableFreelistLeaf = freelist.LeafPageNumbers[0];
                foreach (var leafPage in freelist.LeafPageNumbers)
                    pager.ReadCommittedPage(leafPage).Should().OnlyContain(value => value == 0);
            }

            // Small UPDATE does not allocate; drive freelist reuse with a large INSERT
            // that needs overflow pages (same pattern as IncrementalInsertReusesExistingFreelistLeaf).
            var reusePayload = "storage-review-reuse-" + new string('s', 12_000);
            uint pageCountBeforeReuse;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       readOnly: true))
            {
                pageCountBeforeReuse = pager.CommittedPageCount;
            }

            using (var database = EmbeddedDatabase.OpenFile(databasePath))
            using (var connection = database.Connect())
            {
                Execute(connection, $"UPDATE entries SET payload = '{replacementPayload}' WHERE id = 1;");
                Execute(connection, $"INSERT INTO entries VALUES (2, '{reusePayload}');");
            }

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       databasePath,
                       databasePath + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
                freelist.PageNumbers.Should().NotContain(reusableFreelistLeaf);
                pager.CommittedPageCount.Should().Be(pageCountBeforeReuse);
                // Reused freelist leaf is now live payload/overflow storage, not a free page.
                freelist.LeafPageNumbers.Should().NotContain(reusableFreelistLeaf);
            }

            var verificationPath = databasePath + ".verify.db";
            File.Copy(databasePath, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();
                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeletePhysicalDatabase(verificationPath);
            }

            using var reopened = EmbeddedDatabase.OpenFile(databasePath);
            using var reopenedConnection = reopened.Connect();
            QueryText(reopenedConnection, "SELECT payload FROM entries WHERE id = 1;")
                .Should()
                .Be(replacementPayload);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeletePhysicalDatabase(databasePath);
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    public void StorageReview_FirstCreateFailureCleansNewInMemoryArtifactsAndRetries(int failingWrite)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string databasePath = "storage-review-create-memory.db";
        const string walPath = "storage-review-create-memory.db-wal";
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 17, salt2: 19);
        faults.FailOnOccurrence(FileSystemOperation.Write, failingWrite);

        Assert.Throws<IOException>(() =>
        {
            using var ignored = SqlitePager.Create(fileSystem, databasePath, walPath, walHeader);
        });

        fileSystem.FileExists(databasePath).Should().BeFalse();
        fileSystem.FileExists(walPath).Should().BeFalse();

        using (SqlitePager.Create(fileSystem, databasePath, walPath, walHeader))
        {
        }

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath);
        reopened.CommittedPageCount.Should().Be(1);
    }

    [Test]
    public void StorageReview_FirstCreateFailureCleansNewEncryptedArtifactsAndRetries()
    {
        var faults = new DeterministicFaultInjector();
        var innerFileSystem = new InMemoryFileSystem(faults);
        using var encryption = new AhtolaEncryptionOptions(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(innerFileSystem, encryption);
        const string databasePath = "storage-review-create-encrypted.db";
        const string walPath = "storage-review-create-encrypted.db-wal";
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 23, salt2: 29);
        faults.FailOnOccurrence(FileSystemOperation.Write, 2);

        Assert.Throws<IOException>(() =>
        {
            using var ignored = SqlitePager.Create(fileSystem, databasePath, walPath, walHeader);
        });

        fileSystem.FileExists(databasePath).Should().BeFalse();
        fileSystem.FileExists(walPath).Should().BeFalse();

        using (SqlitePager.Create(fileSystem, databasePath, walPath, walHeader))
        {
        }

        using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath);
        reopened.CommittedPageCount.Should().Be(1);
    }

    [Test]
    public void StorageReview_FirstCreateFailureCleansNewPhysicalArtifactsAndRetries()
    {
        var databasePath = CreatePhysicalDatabasePath();
        var walPath = databasePath + "-wal";
        try
        {
            var fileSystem = new FailOnNthWriteFileSystem(PhysicalFileSystem.Instance, failingWrite: 2);
            var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 31, salt2: 37);

            Assert.Throws<IOException>(() =>
            {
                using var ignored = SqlitePager.Create(fileSystem, databasePath, walPath, walHeader);
            });

            File.Exists(databasePath).Should().BeFalse();
            File.Exists(walPath).Should().BeFalse();

            using (SqlitePager.Create(fileSystem, databasePath, walPath, walHeader))
            {
            }

            using var reopened = SqlitePager.Open(fileSystem, databasePath, walPath);
            reopened.CommittedPageCount.Should().Be(1);
        }
        finally
        {
            DeletePhysicalDatabase(databasePath);
        }
    }

    [Test]
    public void StorageReview_FirstCreateDoesNotDeleteAPreexistingWalArtifact()
    {
        var fileSystem = new InMemoryFileSystem();
        const string databasePath = "storage-review-preexisting.db";
        const string walPath = "storage-review-preexisting.db-wal";
        var walHeader = SqliteWalHeader.Create(SqlitePageSize.Default, salt1: 41, salt2: 43);
        using (SqliteWalFile.Create(fileSystem, walPath, walHeader))
        {
        }

        Assert.Throws<IOException>(() =>
        {
            using var ignored = SqlitePager.Create(fileSystem, databasePath, walPath, walHeader);
        });

        fileSystem.FileExists(databasePath).Should().BeFalse();
        fileSystem.FileExists(walPath).Should().BeTrue();
        using var preservedWal = SqliteWalFile.Open(fileSystem, walPath);
        preservedWal.Header.ToArray().Should().Equal(walHeader.ToArray());
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static string QueryText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static string CreatePhysicalDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "storage-review-defect-regression-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeletePhysicalDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed class FailOnNthWriteFileSystem(IFileSystem inner, int failingWrite) : IFileSystem
    {
        private int _writeCount;

        public bool FileExists(string path) => inner.FileExists(path);

        public IFile OpenFile(string path, FileOpenMode mode, bool readOnly = false)
            => new WriteFailureFile(this, inner.OpenFile(path, mode, readOnly));

        public void DeleteFile(string path) => inner.DeleteFile(path);

        private void BeforeWrite()
        {
            if (Interlocked.Increment(ref _writeCount) == failingWrite)
                throw new IOException("Injected physical first-create write failure.");
        }

        private sealed class WriteFailureFile(FailOnNthWriteFileSystem owner, IFile innerFile) : IFile
        {
            public long Length => innerFile.Length;

            public bool IsReadOnly => innerFile.IsReadOnly;

            public int Read(long position, Span<byte> destination) => innerFile.Read(position, destination);

            public void Write(long position, ReadOnlySpan<byte> source)
            {
                owner.BeforeWrite();
                innerFile.Write(position, source);
            }

            public void SetLength(long length) => innerFile.SetLength(length);

            public void FlushToDisk() => innerFile.FlushToDisk();

            public void Dispose() => innerFile.Dispose();
        }
    }
}
