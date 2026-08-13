using System.Buffers.Binary;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedBoundedTwoInteriorTablePersistencePressureTests
{
    private const int RowCount = 700;
    private const int PayloadLength = 2_048;
    private const int BoundedMutationPageSize = SqlitePageSize.Minimum;
    private const int BoundedMutationRowCount = 700;
    private const int BoundedMutationPayloadLength = 80;
    private const int ThirdInteriorMutationRowCount = 5_000;
    private const int ThirdInteriorMutationPayloadLength = 100;
    private const long ThirdInteriorMutationFirstRowId = 9_000_000_000_000_000_000L;
    private const int FourthInteriorMutationRowCount = 17;
    private const int FourthInteriorMutationPayloadLength = 10;
    private const long FourthInteriorMutationFirstRowId = 9_000_000_000_000_000_000L;
    private const int FifthInteriorMutationRowCount = 33;
    private const int FifthInteriorMutationPayloadLength = 1;
    private const string BoundedMutationEncryptionKey =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void TwoInteriorLevelTablePersistsReopensAndPassesRealSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            CreatePressureTable(path, PhysicalFileSystem.Instance);

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                AssertTwoInteriorLevelTable(pager);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(RowCount);
                QueryText(connection, RowCount).Should().Be(new string('x', PayloadLength));
            }

            var verificationPath = path + ".verify.db";
            File.Copy(path, verificationPath, overwrite: true);
            try
            {
                using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
                sqlite.Open();

                using var integrity = sqlite.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var count = sqlite.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(RowCount);
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeleteDatabase(verificationPath);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedTwoInteriorLevelRewriteRecoversPriorCommittedTable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile("two-interior-wal-failure.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() => Execute(connection, BuildPressureInsert()));
        }

        using var recovered = EmbeddedDatabase.OpenFile("two-interior-wal-failure.db", fileSystem);
        using var recoveredConnection = recovered.Connect();
        QueryCount(recoveredConnection).Should().Be(0);
    }

    [Test]
    public void EncryptedTwoInteriorLevelTableReopensWithEveryRow()
    {
        var innerFileSystem = new InMemoryFileSystem();
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var fileSystem = new AhtolaEncryptionFileSystem(innerFileSystem, encryption);

        CreatePressureTable("two-interior-encrypted.db", fileSystem);

        using (var pager = SqlitePager.Open(
                   fileSystem,
                   "two-interior-encrypted.db",
                   "two-interior-encrypted.db-wal",
                   readOnly: true))
        {
            AssertTwoInteriorLevelTable(pager);
        }

        using var reopened = EmbeddedDatabase.OpenFile("two-interior-encrypted.db", fileSystem);
        using var connection = reopened.Connect();
        QueryCount(connection).Should().Be(RowCount);
        QueryText(connection, RowCount).Should().Be(new string('x', PayloadLength));
    }

    [Test]
    public void NestedLeafMaximumDeleteRewritesOnlyItsNonRootParentSeparatorAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("nested-leaf-delete-integrity");
        try
        {
            CreateBoundedNestedTable(path, PhysicalFileSystem.Instance);
            var target = FindNestedLeafTarget(PhysicalFileSystem.Instance, path);
            byte[] rootBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                rootBefore = pager.ReadCommittedPage(target.RootPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

            AssertNestedLeafDeletion(
                PhysicalFileSystem.Instance,
                path,
                target,
                rootBefore,
                BoundedMutationRowCount - 1);

            VerifyNestedMutationWithSqlite(path, BoundedMutationRowCount - 1, target.DeletedRowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void NestedLeafMaximumDeleteUsesOnlyLeafParentAndPageOneWrites()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "nested-leaf-delete-bounded.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);
        byte[] rootBefore;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            rootBefore = pager.ReadCommittedPage(target.RootPage);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeDelete).Should().Be(7);
        }

        AssertNestedLeafDeletion(
            fileSystem,
            path,
            target,
            rootBefore,
            BoundedMutationRowCount - 1);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(BoundedMutationRowCount - 1);
    }

    [Test]
    public void NestedRightmostLeafMaximumDeletePropagatesItsParentBoundaryToTheRoot()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "nested-rightmost-leaf-delete.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedRightmostLeafTarget(fileSystem, path);
        byte[] parentBefore;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            parentBefore = pager.ReadCommittedPage(target.ParentPage);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

        using var committed = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(committed.ReadCommittedPage(1));
        var root = SqliteTableInteriorPageView.Parse(
            committed.ReadCommittedPage(target.RootPage),
            header.UsableSpace);
        root.Cells[target.RootParentIndex].Cell.LeftChildPage.Should().Be(target.ParentPage);
        root.Cells[target.RootParentIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);
        committed.ReadCommittedPage(target.ParentPage).Should().Equal(parentBefore);

        var leaf = SqliteTableLeafPageView.Parse(
            committed.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
    }

    [Test]
    public void InterruptedNestedLeafParentSeparatorFramesRecoverThePriorCommittedTree()
    {
        for (var failedWrite = 1; failedWrite <= 3; failedWrite++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"nested-leaf-delete-wal-{failedWrite}.db";
            CreateBoundedNestedTable(path, fileSystem);
            var target = FindNestedLeafTarget(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedWrite);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                QueryCount(connection).Should().Be(BoundedMutationRowCount);
                QueryText(connection, target.DeletedRowId).Should().Be(new string('x', BoundedMutationPayloadLength));
            }

            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(target.ParentPage),
                header.UsableSpace);
            parent.Cells[target.ParentCellIndex].Cell.RowId.Should().Be(target.DeletedRowId);
        }
    }

    [Test]
    public void EncryptedNestedLeafMaximumDeleteReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            BoundedMutationEncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-nested-leaf-delete.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(BoundedMutationRowCount - 1);
    }

    [Test]
    public void NestedLeafMaximumDeleteCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "nested-leaf-delete-read-only.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);
        var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = database.Connect())
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeDelete);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(BoundedMutationRowCount);
    }

    [Test]
    public void ReopenRejectsCorruptNestedLeafParentSeparatorBeforeMutation()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "nested-leaf-delete-corrupt.db";
        CreateBoundedNestedTable(path, fileSystem);
        var target = FindNestedLeafTarget(fileSystem, path);
        SqliteDatabaseHeader header;

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var parent = SqliteTableInteriorPageView.Parse(
                store.ReadPage(target.ParentPage),
                header.UsableSpace);
            var corruptedParent = store.ReadPage(target.ParentPage);
            corruptedParent[parent.CellPointers[target.ParentCellIndex] + sizeof(uint)] = 0;
            store.WritePage(target.ParentPage, corruptedParent);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header, salt1: 73, salt2: 79);
        var writesBeforeReopen = faults.GetOperationCount(FileSystemOperation.Write);
        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReopen);
    }

    [Test]
    public void ReopenRejectsCorruptSecondInteriorLevelSeparator()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "two-interior-corrupt.db";
        SqliteDatabaseHeader header;
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert());
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var rootPage = FindTableRootPage(store.ReadPage(1), header);
            var root = SqliteTableInteriorPageView.Parse(
                store.ReadPage(rootPage),
                header.UsableSpace);
            root.Cells.Should().NotBeEmpty();

            var secondInteriorPage = root.Cells[0].Cell.LeftChildPage;
            var secondInterior = SqliteTableInteriorPageView.Parse(
                store.ReadPage(secondInteriorPage),
                header.UsableSpace);
            secondInterior.Cells.Should().NotBeEmpty();

            var pageImage = store.ReadPage(secondInteriorPage);
            pageImage[secondInterior.CellPointers[0] + sizeof(uint)] = 0;
            store.WritePage(secondInteriorPage, pageImage);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header, salt1: 41, salt2: 43);

        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*separator*");
    }

    [Test]
    public void FullRewritePersistsTableWithAtLeastThreeInteriorLevels()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "three-interior-full-rewrite.db";
        const int highDepthRowCount = 1_200;
        const int highDepthPayloadLength = 969;
        const long firstRowId = 9_000_000_000_000_000_000L;
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = 512 };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 47, salt2: 53),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert(
                highDepthRowCount,
                highDepthPayloadLength,
                firstRowId));
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var persistedHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), persistedHeader);
            ReadTableHeight(pager, persistedHeader, rootPage, new HashSet<uint>())
                .Should()
                .BeGreaterThanOrEqualTo(4);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(highDepthRowCount);
        QueryText(reopenedConnection, firstRowId + highDepthRowCount - 1).Should()
            .Be(new string('x', highDepthPayloadLength));
    }

    [Test]
    public void DeletingFromAnOverflowLeafAtTheThirdInteriorLevelFallsBackToTheSafeFullRewrite()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "third-interior-delete-full-rewrite.db";
        const int rowCount = 1_200;
        const int payloadLength = 969;
        const long firstRowId = 9_000_000_000_000_000_000L;
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = BoundedMutationPageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 83, salt2: 89),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert(rowCount, payloadLength, firstRowId));
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var persistedHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), persistedHeader);
            ReadTableHeight(pager, persistedHeader, rootPage, new HashSet<uint>())
                .Should()
                .BeGreaterThanOrEqualTo(4);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM t WHERE id = {firstRowId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBeforeDelete).Should().BeGreaterThan(6);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(rowCount - 1);
        QueryText(reopenedConnection, firstRowId + 1).Should().Be(new string('x', payloadLength));
    }

    [Test]
    public void ThreeInteriorLevelMaximumDeletePropagatesToTheRootReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("three-interior-root-separator");
        try
        {
            CreateBoundedThirdInteriorTable(path, PhysicalFileSystem.Instance);
            var target = FindThirdInteriorRootBoundaryTarget(PhysicalFileSystem.Instance, path);
            byte[] grandparentBefore;
            byte[] parentBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                grandparentBefore = pager.ReadCommittedPage(target.GrandparentPage);
                parentBefore = pager.ReadCommittedPage(target.ParentPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

            AssertThirdInteriorRootBoundaryDeletion(
                PhysicalFileSystem.Instance,
                path,
                target,
                grandparentBefore,
                parentBefore);

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(ThirdInteriorMutationRowCount - 1);
                QueryText(connection, target.ReplacementSeparator)
                    .Should()
                    .Be(new string('x', ThirdInteriorMutationPayloadLength));
            }

            VerifyNestedMutationWithSqlite(
                path,
                ThirdInteriorMutationRowCount - 1,
                target.DeletedRowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ThreeInteriorLevelMaximumDeleteUpdatesItsGrandparentSeparatorWithoutRewritingTheRoot()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "three-interior-grandparent-separator.db";
        CreateBoundedThirdInteriorTable(path, fileSystem);
        var target = FindThirdInteriorGrandparentBoundaryTarget(fileSystem, path);
        byte[] rootBefore;
        byte[] parentBefore;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            rootBefore = pager.ReadCommittedPage(target.RootPage);
            parentBefore = pager.ReadCommittedPage(target.ParentPage);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            pager.ReadCommittedPage(target.RootPage).Should().Equal(rootBefore);
            pager.ReadCommittedPage(target.ParentPage).Should().Equal(parentBefore);
            var grandparent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(target.GrandparentPage),
                header.UsableSpace);
            grandparent.Cells[target.GrandparentParentIndex].Cell.LeftChildPage.Should().Be(target.ParentPage);
            grandparent.Cells[target.GrandparentParentIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);
            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(target.LeafPage),
                header.UsableSpace);
            leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
            leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
            ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(4);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(ThirdInteriorMutationRowCount - 1);
    }

    [Test]
    public void InterruptedThreeInteriorLevelRootSeparatorMutationRecoversThePriorCommittedTree()
    {
        for (var failedWrite = 1; failedWrite <= 3; failedWrite++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"three-interior-root-separator-wal-{failedWrite}.db";
            CreateBoundedThirdInteriorTable(path, fileSystem);
            var target = FindThirdInteriorRootBoundaryTarget(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedWrite);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                QueryCount(connection).Should().Be(ThirdInteriorMutationRowCount);
                QueryText(connection, target.DeletedRowId)
                    .Should()
                    .Be(new string('x', ThirdInteriorMutationPayloadLength));
            }

            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var root = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(target.RootPage),
                header.UsableSpace);
            root.Cells[target.RootGrandparentIndex].Cell.RowId.Should().Be(target.DeletedRowId);
        }
    }

    [Test]
    public void EncryptedThreeInteriorLevelRootSeparatorMutationReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            BoundedMutationEncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-three-interior-root-separator.db";
        CreateBoundedThirdInteriorTable(path, fileSystem);
        var target = FindThirdInteriorRootBoundaryTarget(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        QueryCount(readOnlyConnection).Should().Be(ThirdInteriorMutationRowCount - 1);
        QueryText(readOnlyConnection, target.ReplacementSeparator)
            .Should()
            .Be(new string('x', ThirdInteriorMutationPayloadLength));
    }

    [Test]
    public void ThreeInteriorLevelRootSeparatorMutationCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "three-interior-root-separator-read-only.db";
        CreateBoundedThirdInteriorTable(path, fileSystem);
        var target = FindThirdInteriorRootBoundaryTarget(fileSystem, path);
        var writesBeforeDelete = faults.GetOperationCount(FileSystemOperation.Write);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = database.Connect())
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeDelete);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        QueryCount(reopenedConnection).Should().Be(ThirdInteriorMutationRowCount);
        QueryText(reopenedConnection, target.DeletedRowId)
            .Should()
            .Be(new string('x', ThirdInteriorMutationPayloadLength));
    }

    [Test]
    public void ReopenRejectsCorruptThirdInteriorRootSeparatorBeforeMutation()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "three-interior-root-separator-corrupt.db";
        CreateBoundedThirdInteriorTable(path, fileSystem);
        var target = FindThirdInteriorRootBoundaryTarget(fileSystem, path);
        SqliteDatabaseHeader header;

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            header = store.Header;
            var root = SqliteTableInteriorPageView.Parse(
                store.ReadPage(target.RootPage),
                header.UsableSpace);
            var corruptedRoot = store.ReadPage(target.RootPage);
            corruptedRoot[root.CellPointers[target.RootGrandparentIndex] + sizeof(uint)] = 0;
            store.WritePage(target.RootPage, corruptedRoot);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(fileSystem, path, header, salt1: 97, salt2: 101);
        var writesBeforeReopen = faults.GetOperationCount(FileSystemOperation.Write);
        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<InvalidDataException>().WithMessage("*untracked trailing free gap*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReopen);
    }

    [Test]
    public void FourthInteriorLevelMaximumDeletePropagatesToTheRootReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("four-interior-root-separator");
        try
        {
            CreateBoundedFourthInteriorTable(path, PhysicalFileSystem.Instance);
            var target = FindFourthInteriorRootBoundaryTarget(PhysicalFileSystem.Instance, path);
            byte[] greatGrandparentBefore;
            byte[] grandparentBefore;
            byte[] parentBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                greatGrandparentBefore = pager.ReadCommittedPage(target.GreatGrandparentPage);
                grandparentBefore = pager.ReadCommittedPage(target.GrandparentPage);
                parentBefore = pager.ReadCommittedPage(target.ParentPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

            AssertFourthInteriorRootBoundaryDeletion(
                PhysicalFileSystem.Instance,
                path,
                target,
                greatGrandparentBefore,
                grandparentBefore,
                parentBefore);

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(FourthInteriorMutationRowCount - 1);
                QueryText(connection, target.ReplacementSeparator)
                    .Should()
                    .Be(new string('x', FourthInteriorMutationPayloadLength));
            }

            VerifyNestedMutationWithSqlite(
                path,
                FourthInteriorMutationRowCount - 1,
                target.DeletedRowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void InterruptedFourthInteriorLevelRootSeparatorMutationRecoversThePriorCommittedTree()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "four-interior-root-separator-wal.db";
        CreateBoundedFourthInteriorTable(path, fileSystem);
        var target = FindFourthInteriorRootBoundaryTarget(fileSystem, path);

        for (var failedWrite = 1; failedWrite <= 3; failedWrite++)
        {
            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailNext(FileSystemOperation.Write);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                QueryCount(connection).Should().Be(FourthInteriorMutationRowCount);
                QueryText(connection, target.DeletedRowId)
                    .Should()
                    .Be(new string('x', FourthInteriorMutationPayloadLength));
            }

            using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            var root = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(target.RootPage),
                header.UsableSpace);
            root.Cells[target.RootGreatGrandparentIndex].Cell.RowId.Should().Be(target.DeletedRowId);
        }
    }

    [Test]
    public void FifthInteriorLevelMaximumDeletePropagatesToTheRootReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("five-interior-root-separator");
        try
        {
            CreateBoundedFifthInteriorTable(path, PhysicalFileSystem.Instance);
            var target = FindArbitraryDepthRootBoundaryTarget(
                PhysicalFileSystem.Instance,
                path,
                expectedHeight: 6);
            Dictionary<uint, byte[]> descendantsBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                descendantsBefore = target.DescendantInteriorPages.ToDictionary(
                    pageNumber => pageNumber,
                    pager.ReadCommittedPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM t WHERE id = {target.DeletedRowId};");

            AssertArbitraryDepthRootBoundaryDeletion(
                PhysicalFileSystem.Instance,
                path,
                target,
                descendantsBefore,
                expectedHeight: 6);

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                QueryCount(connection).Should().Be(FifthInteriorMutationRowCount - 1);
                QueryText(connection, target.ReplacementSeparator)
                    .Should()
                    .Be(new string('x', FifthInteriorMutationPayloadLength));
            }

            VerifyNestedMutationWithSqlite(
                path,
                FifthInteriorMutationRowCount - 1,
                target.DeletedRowId);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static void CreateBoundedNestedTable(string path, IFileSystem fileSystem)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = BoundedMutationPageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 0x1020_3040, salt2: 0x5060_7080),
                   header))
        {
        }

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildPressureInsert(BoundedMutationRowCount, BoundedMutationPayloadLength));
    }

    private static void CreateBoundedThirdInteriorTable(string path, IFileSystem fileSystem)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = BoundedMutationPageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1: 0x1122_3344, salt2: 0x5566_7788),
                   header))
        {
        }

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildPressureInsert(
            ThirdInteriorMutationRowCount,
            ThirdInteriorMutationPayloadLength,
            ThirdInteriorMutationFirstRowId));
    }

    private static void CreateBoundedFourthInteriorTable(string path, IFileSystem fileSystem)
        => CreateBoundedInteriorTable(
            path,
            fileSystem,
            interiorLevelCount: 4,
            rowCount: FourthInteriorMutationRowCount,
            payloadLength: FourthInteriorMutationPayloadLength,
            firstRowId: FourthInteriorMutationFirstRowId,
            salt1: 0x99AA_BBCC,
            salt2: 0xDDEE_FF00);

    private static void CreateBoundedFifthInteriorTable(string path, IFileSystem fileSystem)
        => CreateBoundedInteriorTable(
            path,
            fileSystem,
            interiorLevelCount: 5,
            rowCount: FifthInteriorMutationRowCount,
            payloadLength: FifthInteriorMutationPayloadLength,
            firstRowId: 1,
            salt1: 0x2468_ACE0,
            salt2: 0x1357_9BDF);

    private static void CreateBoundedInteriorTable(
        string path,
        IFileSystem fileSystem,
        int interiorLevelCount,
        int rowCount,
        int payloadLength,
        long firstRowId,
        uint salt1,
        uint salt2)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = BoundedMutationPageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1, salt2),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildPressureInsert(
                rowCount,
                payloadLength,
                firstRowId));
        }

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: false);
        var sourceHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), sourceHeader);
        var sourceRootPage = pager.ReadCommittedPage(rootPage);
        var sourceRoot = SqliteTableLeafPageView.Parse(sourceRootPage, sourceHeader.UsableSpace);
        var leafCount = 1 << interiorLevelCount;
        if (rootPage != 2
            || sourceHeader.DatabaseSizeInPages != rootPage
            || sourceRoot.Cells.Count != rowCount
            || rowCount != leafCount + 1
            || sourceRoot.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
        {
            throw new InvalidOperationException(
                "The bounded interior test requires a single non-overflow table root leaf.");
        }

        var leafGroups = new List<IReadOnlyList<SqliteTableLeafCell>>(leafCount);
        var sourceCellIndex = 0;
        for (var leafIndex = 0; leafIndex < leafCount; leafIndex++)
        {
            var cellCount = leafIndex == (leafCount / 2) - 1 ? 2 : 1;
            leafGroups.Add(sourceRoot.Cells
                .Skip(sourceCellIndex)
                .Take(cellCount)
                .Select(cell => cell.Cell)
                .ToArray());
            sourceCellIndex += cellCount;
        }

        if (sourceCellIndex != sourceRoot.Cells.Count)
            throw new InvalidOperationException("The bounded interior test did not consume its source root leaf.");

        var pageImages = new List<(uint PageNumber, byte[] Page)>();
        var children = new List<BoundedInteriorTreeChild>(leafGroups.Count);
        var nextPageNumber = checked(sourceHeader.DatabaseSizeInPages + 1);
        foreach (var leafGroup in leafGroups)
        {
            var builder = new SqliteTableLeafPageBuilder(
                sourceHeader.PageSize,
                sourceHeader.UsableSpace);
            foreach (var cell in leafGroup)
                builder.Append(cell);

            var pageNumber = nextPageNumber++;
            pageImages.Add((pageNumber, builder.Build()));
            children.Add(new BoundedInteriorTreeChild(pageNumber, leafGroup[^1].RowId));
        }

        for (var level = 1; level < interiorLevelCount; level++)
        {
            if (children.Count < 2 || children.Count % 2 != 0)
                throw new InvalidOperationException("The bounded interior test cannot pair its table children.");

            var parents = new List<BoundedInteriorTreeChild>(children.Count / 2);
            for (var childIndex = 0; childIndex < children.Count; childIndex += 2)
            {
                var left = children[childIndex];
                var right = children[childIndex + 1];
                var builder = new SqliteTableInteriorPageBuilder(
                    sourceHeader.PageSize,
                    sourceHeader.UsableSpace,
                    right.PageNumber);
                builder.Append(SqliteTableInteriorCell.Create(left.PageNumber, left.MaximumRowId));

                var pageNumber = nextPageNumber++;
                pageImages.Add((pageNumber, builder.Build()));
                parents.Add(new BoundedInteriorTreeChild(pageNumber, right.MaximumRowId));
            }

            children = parents;
        }

        if (children.Count != 2)
            throw new InvalidOperationException("The bounded interior test requires two root children.");

        var rootBuilder = new SqliteTableInteriorPageBuilder(
            sourceHeader.PageSize,
            sourceHeader.UsableSpace,
            children[1].PageNumber);
        rootBuilder.Append(SqliteTableInteriorCell.Create(
            children[0].PageNumber,
            children[0].MaximumRowId));
        var replacementRootPage = sourceRootPage.ToArray();
        rootBuilder.WriteTo(replacementRootPage);

        var targetPageCount = checked(nextPageNumber - 1);
        var replacementSchemaPage = pager.ReadCommittedPage(1);
        var replacementHeader = sourceHeader with
        {
            ChangeCounter = sourceHeader.ChangeCounter + 1,
            DatabaseSizeInPages = targetPageCount,
            VersionValidFor = sourceHeader.ChangeCounter + 1,
        };
        replacementHeader.WriteTo(replacementSchemaPage);
        using var transaction = pager.BeginTransaction(targetPageCount);
        foreach (var (pageNumber, page) in pageImages)
            transaction.WritePage(pageNumber, page);
        transaction.WritePage(rootPage, replacementRootPage);
        transaction.WritePage(1, replacementSchemaPage);
        transaction.Commit();
    }

    private static ArbitraryDepthRootBoundaryTarget FindArbitraryDepthRootBoundaryTarget(
        IFileSystem fileSystem,
        string path,
        int expectedHeight)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        ReadTableHeight(pager, header, rootPage, new HashSet<uint>()).Should().Be(expectedHeight);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);

        for (var rootChildIndex = 0; rootChildIndex < root.Cells.Count; rootChildIndex++)
        {
            var descendantInteriorPages = new List<uint>();
            var pageNumber = root.Cells[rootChildIndex].Cell.LeftChildPage;
            while (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(pageNumber)).PageType
                   == SqliteBtreePageType.TableInterior)
            {
                descendantInteriorPages.Add(pageNumber);
                var interior = SqliteTableInteriorPageView.Parse(
                    pager.ReadCommittedPage(pageNumber),
                    header.UsableSpace);
                pageNumber = interior.Header.RightMostChildPage;
            }

            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(pageNumber),
                header.UsableSpace);
            if (leaf.Cells.Count < 2)
                continue;

            var deletedRowId = leaf.Cells[^1].Cell.RowId;
            deletedRowId.Should().Be(root.Cells[rootChildIndex].Cell.RowId);
            return new ArbitraryDepthRootBoundaryTarget(
                rootPage,
                rootChildIndex,
                descendantInteriorPages,
                pageNumber,
                deletedRowId,
                leaf.Cells[^2].Cell.RowId);
        }

        throw new InvalidOperationException(
            "Unable to create an arbitrary-depth table with a root-owned multi-cell boundary leaf.");
    }

    private static void AssertArbitraryDepthRootBoundaryDeletion(
        IFileSystem fileSystem,
        string path,
        ArbitraryDepthRootBoundaryTarget target,
        IReadOnlyDictionary<uint, byte[]> descendantsBefore,
        int expectedHeight)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FirstFreelistTrunkPage.Should().Be(0);
        header.FreelistPageCount.Should().Be(0);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(target.RootPage),
            header.UsableSpace);
        root.Cells[target.RootChildIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);
        foreach (var (pageNumber, sourcePage) in descendantsBefore)
            pager.ReadCommittedPage(pageNumber).Should().Equal(sourcePage);

        var leaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
        ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(expectedHeight);
    }

    private static FourthInteriorLeafTarget FindFourthInteriorRootBoundaryTarget(
        IFileSystem fileSystem,
        string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        ReadTableHeight(pager, header, rootPage, new HashSet<uint>()).Should().Be(5);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        for (var greatGrandparentIndex = 0;
             greatGrandparentIndex < root.Cells.Count;
             greatGrandparentIndex++)
        {
            var greatGrandparentPage = root.Cells[greatGrandparentIndex].Cell.LeftChildPage;
            var greatGrandparent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(greatGrandparentPage),
                header.UsableSpace);
            var grandparentPage = greatGrandparent.Header.RightMostChildPage;
            var grandparent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(grandparentPage),
                header.UsableSpace);
            var parentPage = grandparent.Header.RightMostChildPage;
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(parentPage),
                header.UsableSpace);
            var leafPage = parent.Header.RightMostChildPage;
            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(leafPage),
                header.UsableSpace);
            if (leaf.Cells.Count < 2)
                continue;

            var deletedRowId = leaf.Cells[^1].Cell.RowId;
            deletedRowId.Should().Be(root.Cells[greatGrandparentIndex].Cell.RowId);
            return new FourthInteriorLeafTarget(
                rootPage,
                greatGrandparentPage,
                grandparentPage,
                parentPage,
                leafPage,
                greatGrandparentIndex,
                deletedRowId,
                leaf.Cells[^2].Cell.RowId);
        }

        throw new InvalidOperationException(
            "Unable to create a four-interior-level table with a root-owned multi-cell boundary leaf.");
    }

    private static void AssertFourthInteriorRootBoundaryDeletion(
        IFileSystem fileSystem,
        string path,
        FourthInteriorLeafTarget target,
        ReadOnlySpan<byte> greatGrandparentBefore,
        ReadOnlySpan<byte> grandparentBefore,
        ReadOnlySpan<byte> parentBefore)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FirstFreelistTrunkPage.Should().Be(0);
        header.FreelistPageCount.Should().Be(0);
        SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage)
            .PageNumbers
            .Should()
            .BeEmpty();
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(target.RootPage),
            header.UsableSpace);
        root.Cells[target.RootGreatGrandparentIndex].Cell.LeftChildPage.Should()
            .Be(target.GreatGrandparentPage);
        root.Cells[target.RootGreatGrandparentIndex].Cell.RowId.Should()
            .Be(target.ReplacementSeparator);
        pager.ReadCommittedPage(target.GreatGrandparentPage).Should().Equal(greatGrandparentBefore.ToArray());
        pager.ReadCommittedPage(target.GrandparentPage).Should().Equal(grandparentBefore.ToArray());
        pager.ReadCommittedPage(target.ParentPage).Should().Equal(parentBefore.ToArray());
        var leaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
        ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(5);
    }

    private static ThirdInteriorLeafTarget FindThirdInteriorRootBoundaryTarget(
        IFileSystem fileSystem,
        string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        ReadTableHeight(pager, header, rootPage, new HashSet<uint>()).Should().Be(4);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        for (var grandparentIndex = 0; grandparentIndex < root.Cells.Count; grandparentIndex++)
        {
            var grandparentPage = root.Cells[grandparentIndex].Cell.LeftChildPage;
            var grandparent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(grandparentPage),
                header.UsableSpace);
            var parentPage = grandparent.Header.RightMostChildPage;
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(parentPage),
                header.UsableSpace);
            var leafPage = parent.Header.RightMostChildPage;
            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(leafPage),
                header.UsableSpace);
            if (leaf.Cells.Count < 2)
                continue;

            var deletedRowId = leaf.Cells[^1].Cell.RowId;
            deletedRowId.Should().Be(root.Cells[grandparentIndex].Cell.RowId);
            return new ThirdInteriorLeafTarget(
                rootPage,
                grandparentPage,
                parentPage,
                leafPage,
                grandparentIndex,
                grandparent.Cells.Count,
                deletedRowId,
                leaf.Cells[^2].Cell.RowId);
        }

        throw new InvalidOperationException(
            "Unable to create a three-interior-level table with a root-owned multi-cell boundary leaf.");
    }

    private static ThirdInteriorLeafTarget FindThirdInteriorGrandparentBoundaryTarget(
        IFileSystem fileSystem,
        string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        ReadTableHeight(pager, header, rootPage, new HashSet<uint>()).Should().Be(4);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);

        for (var grandparentIndex = 0; grandparentIndex < root.Cells.Count; grandparentIndex++)
        {
            var grandparentPage = root.Cells[grandparentIndex].Cell.LeftChildPage;
            var grandparent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(grandparentPage),
                header.UsableSpace);
            for (var parentIndex = 0; parentIndex < grandparent.Cells.Count; parentIndex++)
            {
                var parentPage = grandparent.Cells[parentIndex].Cell.LeftChildPage;
                var parent = SqliteTableInteriorPageView.Parse(
                    pager.ReadCommittedPage(parentPage),
                    header.UsableSpace);
                var leafPage = parent.Header.RightMostChildPage;
                var leaf = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(leafPage),
                    header.UsableSpace);
                if (leaf.Cells.Count < 2)
                    continue;

                var deletedRowId = leaf.Cells[^1].Cell.RowId;
                deletedRowId.Should().Be(grandparent.Cells[parentIndex].Cell.RowId);
                return new ThirdInteriorLeafTarget(
                    rootPage,
                    grandparentPage,
                    parentPage,
                    leafPage,
                    grandparentIndex,
                    parentIndex,
                    deletedRowId,
                    leaf.Cells[^2].Cell.RowId);
            }
        }

        throw new InvalidOperationException(
            "Unable to create a three-interior-level table with a grandparent-owned multi-cell boundary leaf.");
    }

    private static void AssertThirdInteriorRootBoundaryDeletion(
        IFileSystem fileSystem,
        string path,
        ThirdInteriorLeafTarget target,
        ReadOnlySpan<byte> grandparentBefore,
        ReadOnlySpan<byte> parentBefore)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FirstFreelistTrunkPage.Should().Be(0);
        header.FreelistPageCount.Should().Be(0);
        SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage)
            .PageNumbers
            .Should()
            .BeEmpty();
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(target.RootPage),
            header.UsableSpace);
        root.Cells[target.RootGrandparentIndex].Cell.LeftChildPage.Should().Be(target.GrandparentPage);
        root.Cells[target.RootGrandparentIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);
        pager.ReadCommittedPage(target.GrandparentPage).Should().Equal(grandparentBefore.ToArray());
        pager.ReadCommittedPage(target.ParentPage).Should().Equal(parentBefore.ToArray());
        var leaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
        ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(4);
    }

    private static NestedLeafTarget FindNestedLeafTarget(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        for (var parentIndex = 0; parentIndex < root.Cells.Count; parentIndex++)
        {
            var parentPage = root.Cells[parentIndex].Cell.LeftChildPage;
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(parentPage),
                header.UsableSpace);
            for (var leafIndex = 0; leafIndex < parent.Cells.Count; leafIndex++)
            {
                var leafPage = parent.Cells[leafIndex].Cell.LeftChildPage;
                var leaf = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(leafPage),
                    header.UsableSpace);
                if (leaf.Cells.Count < 2)
                    continue;

                var deletedRowId = leaf.Cells[^1].Cell.RowId;
                deletedRowId.Should().Be(parent.Cells[leafIndex].Cell.RowId);
                return new NestedLeafTarget(
                    rootPage,
                    parentPage,
                    leafPage,
                    parentIndex,
                    leafIndex,
                    deletedRowId,
                    leaf.Cells[^2].Cell.RowId);
            }
        }

        throw new InvalidOperationException(
            "Unable to create a two-interior-level table with a non-rightmost multi-cell leaf.");
    }

    private static NestedLeafTarget FindNestedRightmostLeafTarget(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        for (var parentIndex = 0; parentIndex < root.Cells.Count; parentIndex++)
        {
            var parentPage = root.Cells[parentIndex].Cell.LeftChildPage;
            var parent = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(parentPage),
                header.UsableSpace);
            var leafPage = parent.Header.RightMostChildPage;
            var leaf = SqliteTableLeafPageView.Parse(
                pager.ReadCommittedPage(leafPage),
                header.UsableSpace);
            if (leaf.Cells.Count < 2)
                continue;

            var deletedRowId = leaf.Cells[^1].Cell.RowId;
            deletedRowId.Should().Be(root.Cells[parentIndex].Cell.RowId);
            return new NestedLeafTarget(
                rootPage,
                parentPage,
                leafPage,
                parentIndex,
                parent.Cells.Count,
                deletedRowId,
                leaf.Cells[^2].Cell.RowId);
        }

        throw new InvalidOperationException(
            "Unable to create a two-interior-level table with a multi-cell right-most child leaf.");
    }

    private static void AssertNestedLeafDeletion(
        IFileSystem fileSystem,
        string path,
        NestedLeafTarget target,
        ReadOnlySpan<byte> rootBefore,
        int expectedRowCount)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        header.FirstFreelistTrunkPage.Should().Be(0);
        header.FreelistPageCount.Should().Be(0);
        SqliteFreelist.Read(header, pager.CommittedPageCount, pager.ReadCommittedPage)
            .PageNumbers
            .Should()
            .BeEmpty();
        pager.ReadCommittedPage(target.RootPage).Should().Equal(rootBefore.ToArray());

        var parent = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(target.ParentPage),
            header.UsableSpace);
        parent.Cells[target.ParentCellIndex].Cell.LeftChildPage.Should().Be(target.LeafPage);
        parent.Cells[target.ParentCellIndex].Cell.RowId.Should().Be(target.ReplacementSeparator);

        var leaf = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(target.LeafPage),
            header.UsableSpace);
        leaf.Search(target.DeletedRowId).IsExact.Should().BeFalse();
        leaf.Cells[^1].Cell.RowId.Should().Be(target.ReplacementSeparator);
        ReadTableHeight(pager, header, target.RootPage, new HashSet<uint>()).Should().Be(3);

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connection = database.Connect();
        QueryCount(connection).Should().Be(expectedRowCount);
        QueryText(connection, target.ReplacementSeparator)
            .Should()
            .Be(new string('x', BoundedMutationPayloadLength));
    }

    private static void VerifyNestedMutationWithSqlite(
        string path,
        int expectedRowCount,
        long deletedRowId)
    {
        var verificationPath = CreateDatabasePath("nested-leaf-delete-verify");
        File.Copy(path, verificationPath, overwrite: true);
        try
        {
            using var sqlite = new MsData.SqliteConnection($"Data Source={verificationPath}");
            sqlite.Open();

            using (var integrity = sqlite.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");
            }

            using (var count = sqlite.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM t;";
                Convert.ToInt64(count.ExecuteScalar()).Should().Be(expectedRowCount);
            }

            using var deleted = sqlite.CreateCommand();
            deleted.CommandText = $"SELECT COUNT(*) FROM t WHERE id = {deletedRowId};";
            Convert.ToInt64(deleted.ExecuteScalar()).Should().Be(0);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static void CreatePressureTable(string path, IFileSystem fileSystem)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, BuildPressureInsert());
    }

    private static string BuildPressureInsert(
        int rowCount = RowCount,
        int payloadLength = PayloadLength,
        long firstRowId = 1)
    {
        var payload = new string('x', payloadLength);
        var rows = Enumerable.Range(0, rowCount)
            .Select(offset => $"({firstRowId + offset}, '{payload}')");
        return $"INSERT INTO t VALUES {string.Join(", ", rows)};";
    }

    private static void AssertTwoInteriorLevelTable(SqlitePager pager)
    {
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindTableRootPage(pager.ReadCommittedPage(1), header);
        var root = SqliteTableInteriorPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            header.UsableSpace);
        root.Cells.Should().NotBeEmpty();

        var rowIds = new List<long>();
        foreach (var interiorPage in root.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(root.Header.RightMostChildPage))
        {
            var interior = SqliteTableInteriorPageView.Parse(
                pager.ReadCommittedPage(interiorPage),
                header.UsableSpace);
            interior.Cells.Should().NotBeEmpty();
            foreach (var leafPage in interior.Cells
                         .Select(cell => cell.Cell.LeftChildPage)
                         .Append(interior.Header.RightMostChildPage))
            {
                var leaf = SqliteTableLeafPageView.Parse(
                    pager.ReadCommittedPage(leafPage),
                    header.UsableSpace);
                leaf.Cells.Should().NotBeEmpty();
                rowIds.AddRange(leaf.Cells.Select(cell => cell.Cell.RowId));
            }
        }

        rowIds.Should().Equal(Enumerable.Range(1, RowCount).Select(value => (long)value));
    }

    private static uint FindTableRootPage(ReadOnlySpan<byte> schemaPage, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(
            schemaPage,
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == "t")[3]
            .AsInteger());
    }

    private static int ReadTableHeight(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        ISet<uint> seenPages)
    {
        seenPages.Add(pageNumber).Should().BeTrue();
        var page = pager.ReadCommittedPage(pageNumber);
        var pageHeader = SqliteBtreePageHeader.Parse(page);
        if (pageHeader.PageType == SqliteBtreePageType.TableLeaf)
        {
            var leaf = SqliteTableLeafPageView.Parse(page, header.UsableSpace);
            leaf.Cells.Should().NotBeEmpty();
            return 1;
        }

        pageHeader.PageType.Should().Be(SqliteBtreePageType.TableInterior);
        var interior = SqliteTableInteriorPageView.Parse(page, header.UsableSpace);
        interior.Cells.Should().NotBeEmpty();
        var childHeights = interior.Cells
            .Select(cell => ReadTableHeight(pager, header, cell.Cell.LeftChildPage, seenPages))
            .Append(ReadTableHeight(pager, header, interior.Header.RightMostChildPage, seenPages))
            .ToArray();
        childHeights.Should().OnlyContain(height => height == childHeights[0]);
        return childHeights[0] + 1;
    }

    private static long QueryCount(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT COUNT(*) FROM t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string QueryText(EmbeddedConnection connection, long id)
    {
        using var statement = connection.Prepare($"SELECT value FROM t WHERE id = {id};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-bounded-two-interior-table-persistence-pressure-tests");
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

    private static void ReplaceWalWithEmptyFile(
        IFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader header,
        uint salt1,
        uint salt2)
    {
        fileSystem.DeleteFile(path + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(header.PageSize, salt1, salt2)))
        {
        }
    }

    private sealed record NestedLeafTarget(
        uint RootPage,
        uint ParentPage,
        uint LeafPage,
        int RootParentIndex,
        int ParentCellIndex,
        long DeletedRowId,
        long ReplacementSeparator);

    private sealed record ThirdInteriorLeafTarget(
        uint RootPage,
        uint GrandparentPage,
        uint ParentPage,
        uint LeafPage,
        int RootGrandparentIndex,
        int GrandparentParentIndex,
        long DeletedRowId,
        long ReplacementSeparator);

    private sealed record FourthInteriorLeafTarget(
        uint RootPage,
        uint GreatGrandparentPage,
        uint GrandparentPage,
        uint ParentPage,
        uint LeafPage,
        int RootGreatGrandparentIndex,
        long DeletedRowId,
        long ReplacementSeparator);

    private sealed record ArbitraryDepthRootBoundaryTarget(
        uint RootPage,
        int RootChildIndex,
        IReadOnlyList<uint> DescendantInteriorPages,
        uint LeafPage,
        long DeletedRowId,
        long ReplacementSeparator);

    private sealed record BoundedInteriorTreeChild(uint PageNumber, long MaximumRowId);
}
