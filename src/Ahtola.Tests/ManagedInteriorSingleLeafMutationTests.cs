using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedInteriorSingleLeafMutationTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int PayloadLength = 80;
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void OneLevelLeafUpdateAndLeftMaximumDeleteAreBoundedAndReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "interior-single-leaf-mutation.db";
        CreateMinimumPageDatabase(fileSystem, path);

        long deletedId;
        long updatedId;
        long replacementSeparator;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var before = SeedUntilOneLevelInteriorRoot(connection, fileSystem, path);
            var leftLeaf = ReadLeaf(fileSystem, path, before.ChildPages[0]);
            leftLeaf.Cells.Count.Should().BeGreaterThan(1);
            deletedId = before.Separators[0];
            deletedId.Should().Be(leftLeaf.Cells[^1].Cell.RowId);
            replacementSeparator = leftLeaf.Cells[^2].Cell.RowId;
            updatedId = leftLeaf.Cells[0].Cell.RowId;

            var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeDelete).Should().Be(7);

            var afterDelete = ReadTopology(fileSystem, path);
            afterDelete.RootPage.Should().Be(before.RootPage);
            afterDelete.ChildPages.Should().Equal(before.ChildPages);
            afterDelete.Separators[0].Should().Be(replacementSeparator);
            Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);

            var rootBeforeUpdate = ReadPage(fileSystem, path, afterDelete.RootPage);
            var writesBeforeUpdate = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"UPDATE target SET payload = 'updated-{updatedId:D3}-{new string('u', PayloadLength)}' WHERE id = {updatedId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeUpdate).Should().Be(5);
            ReadPage(fileSystem, path, afterDelete.RootPage).Should().Equal(rootBeforeUpdate);
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);
            Text(connection, $"SELECT payload FROM target WHERE id = {updatedId};")
                .Should()
                .Be($"updated-{updatedId:D3}-{new string('u', PayloadLength)}");
        }
    }

    [Test]
    public void LeftMaximumDeleteReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            CreateMinimumPageDatabase(PhysicalFileSystem.Instance, path);
            long deletedId;
            long expectedSeparator;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                var before = SeedUntilOneLevelInteriorRoot(
                    connection,
                    PhysicalFileSystem.Instance,
                    path);
                var leftLeaf = ReadLeaf(PhysicalFileSystem.Instance, path, before.ChildPages[0]);
                deletedId = before.Separators[0];
                expectedSeparator = leftLeaf.Cells[^2].Cell.RowId;
                Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
                Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);

            VerifyWithSqlite(path, expectedSeparator);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedLeftMaximumDeleteRecoversPriorLeafAndParentSeparator()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "interior-single-leaf-delete-recovery.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long deletedId;
        int expectedCount;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var before = SeedUntilOneLevelInteriorRoot(connection, fileSystem, path);
            deletedId = before.Separators[0];
            expectedCount = checked((int)Integer(connection, "SELECT COUNT(*) FROM target;"));

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 2);
            Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM target WHERE id = {deletedId};"));
        }

        using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = recovered.Connect())
        {
            Integer(connection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
            Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(1);
        }

        var recoveredTopology = ReadTopology(fileSystem, path);
        recoveredTopology.Separators[0].Should().Be(deletedId);
    }

    [Test]
    public void EncryptedOneLevelLeftMaximumDeleteReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-interior-single-leaf-delete.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long deletedId;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var before = SeedUntilOneLevelInteriorRoot(connection, fileSystem, path);
            deletedId = before.Separators[0];
            Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);
    }

    [Test]
    public void TwoChildRootDeleteUnderMinimumPagePressureCollapsesAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("collapse-integrity");
        try
        {
            CreateMinimumPageDatabase(PhysicalFileSystem.Instance, path);
            CollapseTopology before;
            long deletedId;
            long expectedCount;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                _ = SeedUntilTwoChildInteriorRoot(connection, PhysicalFileSystem.Instance, path);
                before = ReadCollapseTopology(PhysicalFileSystem.Instance, path);
                deletedId = Integer(connection, "SELECT max(id) FROM target;");
                expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;") - 1;

                Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
            }

            var after = ReadCollapseTopology(PhysicalFileSystem.Instance, path);
            after.RootPage.Should().Be(before.RootPage);
            after.PageCount.Should().Be(before.PageCount);
            after.RootType.Should().Be(SqliteBtreePageType.TableLeaf);
            after.Header.FreelistPageCount.Should().Be(2);
            after.FreelistPages.Should().Equal(before.ChildPages.Order());
            after.FreelistTrunkPages.Should().ContainSingle();
            after.FreelistLeafPages.Should().ContainSingle();
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(after.RootPage),
                    after.Header.UsableSpace).Cells.Should().HaveCount(checked((int)expectedCount));
                foreach (var leafPage in after.FreelistLeafPages)
                    pager.ReadCommittedPage(leafPage).Should().OnlyContain(value => value == 0);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);
                Integer(connection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
            }

            VerifyTableIntegrityWithSqlite(path, expectedCount, after.Header.FreelistPageCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedTwoChildRootCollapseFrameRecoversThePriorInteriorRoot()
    {
        for (var failedFrame = 1; failedFrame <= 4; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"two-child-root-collapse-wal-{failedFrame}.db";
            CreateMinimumPageDatabase(fileSystem, path);
            CollapseTopology before;
            long deletedId;
            long expectedCount;

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                _ = SeedUntilTwoChildInteriorRoot(connection, fileSystem, path);
                before = ReadCollapseTopology(fileSystem, path);
                deletedId = Integer(connection, "SELECT max(id) FROM target;");
                expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;");

                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM target WHERE id = {deletedId};"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Integer(connection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
                Integer(connection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(1);
            }

            var recoveredTopology = ReadCollapseTopology(fileSystem, path);
            recoveredTopology.RootPage.Should().Be(before.RootPage);
            recoveredTopology.PageCount.Should().Be(before.PageCount);
            recoveredTopology.RootType.Should().Be(SqliteBtreePageType.TableInterior);
            recoveredTopology.ChildPages.Should().Equal(before.ChildPages);
            recoveredTopology.Header.FreelistPageCount.Should().Be(0);
            recoveredTopology.FreelistPages.Should().BeEmpty();
        }
    }

    [Test]
    public void EncryptedTwoChildRootCollapseReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-two-child-root-collapse.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long deletedId;
        long expectedCount;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            _ = SeedUntilTwoChildInteriorRoot(connection, fileSystem, path);
            deletedId = Integer(connection, "SELECT max(id) FROM target;");
            expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;") - 1;
            Execute(connection, $"DELETE FROM target WHERE id = {deletedId};");
        }

        var collapsed = ReadCollapseTopology(fileSystem, path);
        collapsed.RootType.Should().Be(SqliteBtreePageType.TableLeaf);
        collapsed.Header.FreelistPageCount.Should().Be(2);

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        Integer(readOnlyConnection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
        Integer(readOnlyConnection, $"SELECT COUNT(*) FROM target WHERE id = {deletedId};").Should().Be(0);
    }

    [Test]
    public void ThreeChildRootSingletonDeleteRemovesChildReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("empty-child-removal-integrity");
        try
        {
            CreateMinimumPageDatabase(PhysicalFileSystem.Instance, path);
            CollapseTopology before;
            long expectedCount;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                _ = SeedUntilThreeChildInteriorRootWithSingletonLeftLeaf(
                    connection,
                    PhysicalFileSystem.Instance,
                    path);
                before = ReadCollapseTopology(PhysicalFileSystem.Instance, path);
                expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;") - 1;

                Execute(connection, "DELETE FROM target WHERE id = 1;");
            }

            var after = ReadCollapseTopology(PhysicalFileSystem.Instance, path);
            after.RootPage.Should().Be(before.RootPage);
            after.PageCount.Should().Be(before.PageCount);
            after.RootType.Should().Be(SqliteBtreePageType.TableInterior);
            after.ChildPages.Should().Equal(before.ChildPages.Skip(1));
            after.Header.FreelistPageCount.Should().Be(1);
            after.FreelistPages.Should().Equal(before.ChildPages[0]);
            after.FreelistTrunkPages.Should().Equal(before.ChildPages[0]);
            after.FreelistLeafPages.Should().BeEmpty();
            after.ChildPages
                .SelectMany(page => ReadLeaf(PhysicalFileSystem.Instance, path, page).Cells)
                .Select(cell => cell.Cell.RowId)
                .Should()
                .Equal(Enumerable.Range(2, checked((int)expectedCount)).Select(id => (long)id));

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Integer(connection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
                Integer(connection, "SELECT COUNT(*) FROM target WHERE id = 1;").Should().Be(0);
            }

            VerifyTableIntegrityWithSqlite(path, expectedCount, after.Header.FreelistPageCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ThreeChildRootRightmostSingletonDeleteRemovesTrailingParentSeparator()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "three-child-rightmost-empty-child-removal.db";
        CreateMinimumPageDatabase(fileSystem, path);
        Topology before;
        long expectedCount;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            before = SeedThreeChildInteriorRootWithSingletonRightLeaf(connection, fileSystem, path);
            expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;") - 1;
            Execute(connection, "DELETE FROM target WHERE id = 4;");
        }

        var after = ReadCollapseTopology(fileSystem, path);
        var afterRouting = ReadTopology(fileSystem, path);
        after.RootPage.Should().Be(before.RootPage);
        after.RootType.Should().Be(SqliteBtreePageType.TableInterior);
        after.ChildPages.Should().Equal(before.ChildPages.Take(before.ChildPages.Count - 1));
        afterRouting.Separators.Should().Equal(before.Separators.Take(before.Separators.Count - 1));
        after.Header.FreelistPageCount.Should().Be(1);
        after.FreelistPages.Should().Equal(before.ChildPages[^1]);
        after.ChildPages
            .SelectMany(page => ReadLeaf(fileSystem, path, page).Cells)
            .Select(cell => cell.Cell.RowId)
            .Should()
            .Equal(1, 2, 3);

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target WHERE id = 4;").Should().Be(0);
    }

    [Test]
    public void EveryInterruptedThreeChildSingletonDeleteRecoversThePriorRootAndFreelist()
    {
        for (var failedWrite = 1; failedWrite <= 3; failedWrite++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"three-child-empty-child-removal-wal-{failedWrite}.db";
            CreateMinimumPageDatabase(fileSystem, path);
            Topology before;
            long expectedCount;

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                before = SeedUntilThreeChildInteriorRootWithSingletonLeftLeaf(connection, fileSystem, path);
                expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;");

                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedWrite);
                Assert.Throws<IOException>(() => Execute(connection, "DELETE FROM target WHERE id = 1;"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Integer(connection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
                Integer(connection, "SELECT COUNT(*) FROM target WHERE id = 1;").Should().Be(1);
            }

            var recoveredTopology = ReadCollapseTopology(fileSystem, path);
            recoveredTopology.RootPage.Should().Be(before.RootPage);
            recoveredTopology.RootType.Should().Be(SqliteBtreePageType.TableInterior);
            recoveredTopology.ChildPages.Should().Equal(before.ChildPages);
            recoveredTopology.Header.FreelistPageCount.Should().Be(0);
            recoveredTopology.FreelistPages.Should().BeEmpty();
        }
    }

    [Test]
    public void EncryptedThreeChildSingletonDeleteReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-three-child-empty-child-removal.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long expectedCount;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            _ = SeedUntilThreeChildInteriorRootWithSingletonLeftLeaf(connection, fileSystem, path);
            expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;") - 1;
            Execute(connection, "DELETE FROM target WHERE id = 1;");
        }

        var after = ReadCollapseTopology(fileSystem, path);
        after.RootType.Should().Be(SqliteBtreePageType.TableInterior);
        after.Header.FreelistPageCount.Should().Be(1);

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        Integer(readOnlyConnection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
        Integer(readOnlyConnection, "SELECT COUNT(*) FROM target WHERE id = 1;").Should().Be(0);
    }

    [Test]
    public void ThreeChildSingletonDeleteCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "three-child-empty-child-removal-read-only.db";
        CreateMinimumPageDatabase(fileSystem, path);
        Topology before;
        long expectedCount;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            before = SeedUntilThreeChildInteriorRootWithSingletonLeftLeaf(connection, fileSystem, path);
            expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;");
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "DELETE FROM target WHERE id = 1;"));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        var unchanged = ReadCollapseTopology(fileSystem, path);
        unchanged.RootPage.Should().Be(before.RootPage);
        unchanged.RootType.Should().Be(SqliteBtreePageType.TableInterior);
        unchanged.ChildPages.Should().Equal(before.ChildPages);
        unchanged.Header.FreelistPageCount.Should().Be(0);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
    }

    [Test]
    public void TwoChildRootCollapseCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "two-child-root-collapse-read-only.db";
        CreateMinimumPageDatabase(fileSystem, path);
        long deletedId;
        long expectedCount;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            _ = SeedUntilTwoChildInteriorRoot(connection, fileSystem, path);
            deletedId = Integer(connection, "SELECT max(id) FROM target;");
            expectedCount = Integer(connection, "SELECT COUNT(*) FROM target;");
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(connection, $"DELETE FROM target WHERE id = {deletedId};"));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        var unchanged = ReadCollapseTopology(fileSystem, path);
        unchanged.RootType.Should().Be(SqliteBtreePageType.TableInterior);
        unchanged.Header.FreelistPageCount.Should().Be(0);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(expectedCount);
    }

    [Test]
    public void CorruptTwoChildRootCollapseChildIsRejectedBeforeAnyMutationWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "two-child-root-collapse-corruption.db";
        CreateMinimumPageDatabase(fileSystem, path);
        Topology topology;

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            topology = SeedUntilTwoChildInteriorRoot(connection, fileSystem, path);

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(topology.ChildPages[0]);
            page[0] = (byte)SqliteBtreePageType.IndexLeaf;
            store.WritePage(topology.ChildPages[0], page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    private static Topology SeedUntilOneLevelInteriorRoot(
        EmbeddedConnection connection,
        IFileSystem fileSystem,
        string path)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");
        Execute(connection, BuildInsert(1, 120));
        var topology = ReadTopology(fileSystem, path);
        if (topology.RootType != SqliteBtreePageType.TableInterior
            || topology.Separators.Count == 0)
        {
            throw new InvalidOperationException("Unable to create a one-level table-interior root.");
        }

        return topology;
    }

    private static Topology SeedUntilTwoChildInteriorRoot(
        EmbeddedConnection connection,
        IFileSystem fileSystem,
        string path)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");
        for (var id = 1; id <= 128; id++)
        {
            Execute(connection, InsertStatement(id));
            var topology = ReadTopology(fileSystem, path);
            if (topology.RootType == SqliteBtreePageType.TableInterior
                && topology.Separators.Count == 1
                && topology.ChildPages.Count == 2)
            {
                return topology;
            }
        }

        throw new InvalidOperationException("Unable to create a two-child table-interior root.");
    }

    private static Topology SeedUntilThreeChildInteriorRootWithSingletonLeftLeaf(
        EmbeddedConnection connection,
        IFileSystem fileSystem,
        string path)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");
        Execute(connection, $"INSERT INTO target VALUES (1, '{new string('l', 400)}');");
        for (var id = 2; id <= 128; id++)
        {
            Execute(connection, InsertStatement(id));
            var topology = ReadTopology(fileSystem, path);
            if (topology.RootType != SqliteBtreePageType.TableInterior
                || topology.ChildPages.Count < 3)
            {
                continue;
            }

            var leftLeaf = ReadLeaf(fileSystem, path, topology.ChildPages[0]);
            if (leftLeaf.Cells.Count == 1 && leftLeaf.Cells[0].Cell.RowId == 1)
                return topology;
        }

        throw new InvalidOperationException(
            "Unable to create a three-child table-interior root with a singleton left leaf.");
    }

    private static Topology SeedThreeChildInteriorRootWithSingletonRightLeaf(
        EmbeddedConnection connection,
        IFileSystem fileSystem,
        string path)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, payload TEXT);");
        Execute(connection, InsertStatement(1));
        Execute(connection, InsertStatement(2));
        Execute(connection, $"INSERT INTO target VALUES (3, '{new string('m', 400)}');");
        Execute(connection, InsertStatement(4));

        var topology = ReadTopology(fileSystem, path);
        if (topology.RootType != SqliteBtreePageType.TableInterior
            || topology.ChildPages.Count != 3)
        {
            throw new InvalidOperationException("Unable to create a three-child table-interior root.");
        }

        var rightLeaf = ReadLeaf(fileSystem, path, topology.ChildPages[^1]);
        if (rightLeaf.Cells.Count != 1 || rightLeaf.Cells[0].Cell.RowId != 4)
        {
            throw new InvalidOperationException(
                "Unable to create a three-child table-interior root with a singleton right leaf.");
        }

        return topology;
    }

    private static void CreateMinimumPageDatabase(IFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = PageSize };
        using var pager = SqlitePager.Create(
            fileSystem,
            path,
            path + "-wal",
            SqliteWalHeader.Create(PageSize, salt1: 0x1020_3040, salt2: 0x5060_7080),
            header);
    }

    private static Topology ReadTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindRootPage(pager, header);
        var root = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage));
        if (root.PageType != SqliteBtreePageType.TableInterior)
            return new Topology(rootPage, root.PageType, [], []);

        var interior = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        return new Topology(
            rootPage,
            root.PageType,
            interior.Cells.Select(cell => cell.Cell.RowId).ToArray(),
            interior.Cells.Select(cell => cell.Cell.LeftChildPage)
                .Append(interior.Header.RightMostChildPage)
                .ToArray());
    }

    private static SqliteTableLeafPageView ReadLeaf(IFileSystem fileSystem, string path, uint pageNumber)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        return SqliteTableLeafPageView.Parse(pager.ReadCommittedPage(pageNumber), header.UsableSpace);
    }

    private static byte[] ReadPage(IFileSystem fileSystem, string path, uint pageNumber)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        return pager.ReadCommittedPage(pageNumber);
    }

    private static uint FindRootPage(SqlitePager pager, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == "target")[3]
            .AsInteger());
    }

    private static void VerifyWithSqlite(string path, long expectedSeparator)
    {
        var verificationPath = CreateDatabasePath("integrity");
        try
        {
            File.Copy(path, verificationPath, overwrite: true);

            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using var root = sqlite.CreateCommand();
            root.CommandText = $"SELECT max(rowid) FROM target WHERE rowid < {expectedSeparator + 1};";
            Convert.ToInt64(root.ExecuteScalar()).Should().Be(expectedSeparator);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static CollapseTopology ReadCollapseTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindRootPage(pager, header);
        var root = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(rootPage));
        uint[] childPages;
        if (root.PageType == SqliteBtreePageType.TableInterior)
        {
            var interior = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(rootPage),
                header.UsableSpace);
            childPages = interior.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(interior.Header.RightMostChildPage)
                .ToArray();
        }
        else
        {
            childPages = [];
        }

        var freelist = SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage);
        return new CollapseTopology(
            pager.CommittedPageCount,
            rootPage,
            root.PageType,
            header,
            childPages,
            freelist.PageNumbers.ToArray(),
            freelist.TrunkPageNumbers.ToArray(),
            freelist.LeafPageNumbers.ToArray());
    }

    private static void VerifyTableIntegrityWithSqlite(string path, long expectedCount, uint expectedFreelistCount)
    {
        var verificationPath = CreateDatabasePath("collapse-integrity");
        try
        {
            File.Copy(path, verificationPath, overwrite: true);
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();
            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using (var count = sqlite.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM target;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(expectedCount);
            }

            using var freelistCount = sqlite.CreateCommand();
            freelistCount.CommandText = "PRAGMA freelist_count;";
            Convert.ToUInt32(freelistCount.ExecuteScalar()).Should().Be(expectedFreelistCount);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static string InsertStatement(int id)
        => $"INSERT INTO target VALUES ({id}, 'payload-{id:D3}-{new string('x', PayloadLength)}');";

    private static string BuildInsert(int firstId, int count)
        => $"INSERT INTO target VALUES {string.Join(", ", Enumerable.Range(firstId, count).Select(
            id => $"({id}, 'payload-{id:D3}-{new string('x', PayloadLength)}')"))};";

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Integer(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string Text(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-interior-single-leaf-mutation-tests");
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

    private sealed record Topology(
        uint RootPage,
        SqliteBtreePageType RootType,
        IReadOnlyList<long> Separators,
        IReadOnlyList<uint> ChildPages);

    private sealed record CollapseTopology(
        uint PageCount,
        uint RootPage,
        SqliteBtreePageType RootType,
        SqliteDatabaseHeader Header,
        IReadOnlyList<uint> ChildPages,
        IReadOnlyList<uint> FreelistPages,
        IReadOnlyList<uint> FreelistTrunkPages,
        IReadOnlyList<uint> FreelistLeafPages);
}
