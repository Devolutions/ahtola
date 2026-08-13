using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedFreelistLeafReuseStorageTests
{
    private const string DatabasePath = "freelist-leaf-reuse.db";
    private const string RetiredPrefix = "retired-page-bytes-";
    private const string ReplacementPrefix = "replacement-page-bytes-";
    private static readonly byte[] Aes256Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    [Test]
    public void IncrementalUpdateReusesValidatedFreelistLeavesWithoutGrowingTheFile()
    {
        var fileSystem = new InMemoryFileSystem();
        var reusableLeaf = PrepareFreeLeafDatabase(fileSystem);
        uint pageCountBefore;
        using (var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal", readOnly: true))
            pageCountBefore = pager.CommittedPageCount;

        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            // Large replacement needs overflow pages and must prefer freelist leaves.
            Execute(connection, $"UPDATE records SET value = '{ReplacementPrefix}{new string('r', 12_000)}' WHERE id = 1;");
        }

        using (var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            header.DatabaseSizeInPages.Should().Be(pageCountBefore);
            var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
            freelist.PageNumbers.Should().NotContain(reusableLeaf);
            foreach (var leafPage in freelist.LeafPageNumbers)
                pager.ReadCommittedPage(leafPage).Should().OnlyContain(value => value == 0);
        }

        using var reopened = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryText(reopenedConnection, "SELECT value FROM records WHERE id = 1;")
            .Should()
            .StartWith(ReplacementPrefix);
    }

    [Test]
    public void ReuseWalWriteFailureRecoversThePriorFreelistPartition()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var reusableLeaf = PrepareFreeLeafDatabase(fileSystem);

        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() =>
                Execute(connection, $"UPDATE records SET value = '{ReplacementPrefix}{new string('r', 64)}' WHERE id = 1;"));
        }

        using (var reopened = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = reopened.Connect())
        {
            QueryText(connection, "SELECT value FROM records WHERE id = 1;")
                .Should()
                .StartWith("small-");
        }

        using var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage)
            .LeafPageNumbers
            .Should()
            .Contain(reusableLeaf);
    }

    [Test]
    public void EncryptedIncrementalUpdateReusesFreelistLeavesAndAuthenticatesAfterReopen()
    {
        using var encryption = new AhtolaEncryptionOptions(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        var reusableLeaf = PrepareFreeLeafDatabase(fileSystem);
        uint pageCountBefore;
        using (var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal", readOnly: true))
            pageCountBefore = pager.CommittedPageCount;

        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, $"UPDATE records SET value = '{ReplacementPrefix}{new string('e', 12_000)}' WHERE id = 1;");
        }

        using (var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            header.DatabaseSizeInPages.Should().Be(pageCountBefore);
            var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
            freelist.PageNumbers.Should().NotContain(reusableLeaf);
            foreach (var leafPage in freelist.LeafPageNumbers)
                pager.ReadCommittedPage(leafPage).Should().OnlyContain(value => value == 0);
        }

        using var reopened = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryText(reopenedConnection, "SELECT value FROM records WHERE id = 1;")
            .Should()
            .StartWith(ReplacementPrefix);
    }

    [Test]
    public void ReopenFailsClosedWhenAFreelistLeafAliasesAnActivePage()
    {
        var fileSystem = new InMemoryFileSystem();
        _ = PrepareFreeLeafDatabase(fileSystem);
        SqliteDatabaseHeader header;
        SqliteFreelist freelist;

        using (var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal"))
        {
            header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
        }

        using (var store = SqlitePageStore.Open(fileSystem, DatabasePath))
        {
            var trunk = store.ReadPage(freelist.FirstTrunkPage);
            BinaryPrimitives.WriteUInt32BigEndian(trunk.AsSpan(2 * sizeof(uint)), 2);
            store.WritePage(freelist.FirstTrunkPage, trunk);
            store.Flush();
        }

        fileSystem.DeleteFile(DatabasePath + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   DatabasePath + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 73, salt2: 79)))
        {
        }

        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))!
            .Message
            .Should()
            .Contain("allocation map");
    }

    private static uint PrepareFreeLeafDatabase(IFileSystem fileSystem)
    {
        using (var database = EmbeddedDatabase.OpenFile(DatabasePath, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE records(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, $"INSERT INTO records VALUES (1, '{RetiredPrefix}{new string('q', 12_000)}');");
            Execute(connection, "UPDATE records SET value = 'small-committed' WHERE id = 1;");
        }

        using var pager = SqlitePager.Open(fileSystem, DatabasePath, DatabasePath + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
        freelist.LeafPageNumbers.Should().NotBeEmpty();
        return freelist.LeafPageNumbers[0];
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

}
