using System.Buffers.Binary;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSecondaryIndexLeafDeletionReclamationTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int InitialRowCount = 5;
    private const int MaximumRowCount = 64;
    private const string IndexName = "target_code_repeated";
    private static readonly string[] MultiIndexNames =
        ["target_code_repeated_one", "target_code_repeated_two", "target_code_repeated_three"];
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void SingletonSecondaryIndexLeafDeletionReclaimsItsPageReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("integrity");
        try
        {
            var target = SeedSingletonReclamationTopology(PhysicalFileSystem.Instance, path);
            var before = ReadTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};");

            var after = ReadTopology(PhysicalFileSystem.Instance, path);
            AssertReclaimedLeaf(before, after, target);
            var reclaimedPage = ReclaimedPage(before, after);
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var freelist = SqliteFreelist.Read(
                    after.Header,
                    pager.CommittedPageCount,
                    pager.ReadCommittedPage);
                freelist.PageNumbers.Should().Equal([reclaimedPage]);
                freelist.TrunkPageNumbers.Should().Equal([reclaimedPage]);
                BinaryPrimitives.ReadUInt32BigEndian(pager.ReadCommittedPage(reclaimedPage))
                    .Should()
                    .Be(0);
                BinaryPrimitives.ReadUInt32BigEndian(
                        pager.ReadCommittedPage(reclaimedPage).AsSpan(sizeof(uint)))
                    .Should()
                    .Be(0);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount - 1);
                CountById(connection, target.RowId).Should().Be(0);
                RowId(connection, SurvivingRowId(target)).Should().Be(SurvivingRowId(target));
            }

            VerifyWithSqlite(path, target);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryInterruptedSecondaryIndexLeafReclamationFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= 5; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"secondary-index-leaf-reclamation-wal-{failedFrame}.db";
            var target = SeedSingletonReclamationTopology(fileSystem, path);
            var before = ReadTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Count(connection).Should().Be(target.RowCount);
                CountById(connection, target.RowId).Should().Be(1);
                RowId(connection, SurvivingRowId(target)).Should().Be(SurvivingRowId(target));
            }

            var after = ReadTopology(fileSystem, path);
            after.Header.Should().Be(before.Header);
            after.PageCount.Should().Be(before.PageCount);
            after.TableRootPage.Should().Be(before.TableRootPage);
            after.TableRootType.Should().Be(before.TableRootType);
            after.IndexRootPage.Should().Be(before.IndexRootPage);
            after.IndexRootType.Should().Be(before.IndexRootType);
            after.IndexRootCellCount.Should().Be(before.IndexRootCellCount);
            after.Children.Should().BeEquivalentTo(before.Children, options => options.WithStrictOrdering());
        }
    }

    [Test]
    public void EncryptedSecondaryIndexLeafDeletionReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-secondary-index-leaf-reclamation.db";
        var target = SeedDirectLeafTopology(fileSystem, path, repeatedColumns: 20);
        var before = ReadTopology(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};");

        AssertLeafDeletion(before, ReadTopology(fileSystem, path), target);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var readOnlyConnection = reopened.Connect();
        Count(readOnlyConnection).Should().Be(target.RowCount - 1);
        CountById(readOnlyConnection, target.RowId).Should().Be(0);
        RowId(readOnlyConnection, SurvivingRowId(target)).Should().Be(SurvivingRowId(target));
    }

    [Test]
    public void SecondaryIndexLeafDeletionCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "secondary-index-leaf-reclamation-read-only.db";
        var target = SeedDirectLeafTopology(fileSystem, path);
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);

        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};"));

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Count(reopenedConnection).Should().Be(target.RowCount);
        CountById(reopenedConnection, target.RowId).Should().Be(1);
    }

    [Test]
    public void ReopenRejectsAliasedSecondaryIndexChildBeforeLeafDeletion()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "secondary-index-leaf-reclamation-corrupt.db";
        SeedDirectLeafTopology(fileSystem, path);
        var topology = ReadTopology(fileSystem, path);

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var root = store.ReadPage(topology.IndexRootPage);
            var parent = SqliteIndexInteriorPageView.Parse(
                root,
                topology.Header.UsableSpace,
                topology.Header.TextEncoding);
            BinaryPrimitives.WriteUInt32BigEndian(
                root.AsSpan(parent.CellPointers[0], sizeof(uint)),
                parent.Header.RightMostChildPage);
            store.WritePage(topology.IndexRootPage, root);
            store.Flush();
        }

        fileSystem.DeleteFile(path + "-wal");
        using (SqliteWalFile.Create(
                   fileSystem,
                   path + "-wal",
                   SqliteWalHeader.Create(PageSize, salt1: 53, salt2: 59)))
        {
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        var reopen = () => EmbeddedDatabase.OpenFile(path, fileSystem);
        reopen.Should().Throw<EmbeddedSqlException>().WithMessage("*index*");
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void MultiIndexInteriorLeafDeletionPersistsReopensAndPassesSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath("multi-index-integrity");
        try
        {
            var target = SeedMultiIndexDirectLeafTopology(PhysicalFileSystem.Instance, path);

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};");

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Count(connection).Should().Be(target.RowCount - 1);
                CountById(connection, target.RowId).Should().Be(0);
                RowId(connection, checked((int)SurvivingRowId(target.RowId)))
                    .Should()
                    .Be(SurvivingRowId(target.RowId));
            }

            VerifyMultiIndexWithSqlite(path, target);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void MultiIndexInteriorLeafDeletionCommitsOnlyItsTableAndThreeIndexLeaves()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-interior-leaf-deletion.db";
        var target = SeedMultiIndexDirectLeafTopology(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(11);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Count(reopenedConnection).Should().Be(target.RowCount - 1);
        CountById(reopenedConnection, target.RowId).Should().Be(0);
    }

    [Test]
    public void EveryInterruptedMultiIndexInteriorLeafDeletionFrameRecoversThePriorTree()
    {
        for (var failedFrame = 1; failedFrame <= MultiIndexNames.Length + 2; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"multi-index-interior-leaf-deletion-wal-{failedFrame}.db";
            var target = SeedMultiIndexDirectLeafTopology(fileSystem, path);

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() => Execute(connection, $"DELETE FROM target WHERE id = {target.RowId};"));
            }

            using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var recoveredConnection = recovered.Connect();
            Count(recoveredConnection).Should().Be(target.RowCount);
            CountById(recoveredConnection, target.RowId).Should().Be(1);
            RowId(recoveredConnection, checked((int)target.RowId)).Should().Be(target.RowId);
        }
    }

    private static SingletonLeafTarget SeedDirectLeafTopology(
        IFileSystem fileSystem,
        string path,
        int repeatedColumns = 48,
        int minimumChildCount = 2)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY);");
            Execute(connection, InsertStatement(Enumerable.Range(1, InitialRowCount)));
            Execute(connection, $"CREATE UNIQUE INDEX {IndexName} ON target({RepeatedIndexColumns(repeatedColumns)});");
        }

        var initialTopology = ReadTopology(fileSystem, path);
        if (TryGetDirectLeafTarget(
                initialTopology,
                InitialRowCount,
                minimumChildCount,
                out var initialTarget))
            return initialTarget;

        for (var id = InitialRowCount + 1; id <= MaximumRowCount; id++)
        {
            using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var connection = database.Connect();
            Execute(connection, InsertStatement([id]));

            var topology = ReadTopology(fileSystem, path);
            if (TryGetDirectLeafTarget(topology, id, minimumChildCount, out var target))
                return target;
        }

        throw new InvalidOperationException("Unable to create a direct secondary-index leaf for deletion.");
    }

    private static MultiIndexLeafTarget SeedMultiIndexDirectLeafTopology(
        IFileSystem fileSystem,
        string path)
    {
        CreateMinimumPageDatabase(fileSystem, path);
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY);");
            Execute(connection, InsertStatement(Enumerable.Range(1, InitialRowCount)));
            foreach (var indexName in MultiIndexNames)
                Execute(connection, $"CREATE UNIQUE INDEX {indexName} ON target({RepeatedIndexColumns(48)});");
        }

        for (var rowCount = InitialRowCount; rowCount <= MaximumRowCount; rowCount++)
        {
            if (TryFindMultiIndexLeafTarget(fileSystem, path, rowCount, out var target))
                return target;

            if (rowCount == MaximumRowCount)
                break;

            using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            using var connection = database.Connect();
            Execute(connection, InsertStatement([rowCount + 1]));
        }

        throw new InvalidOperationException("Unable to create safe direct leaves for every secondary index.");
    }

    private static bool TryFindMultiIndexLeafTarget(
        IFileSystem fileSystem,
        string path,
        int rowCount,
        out MultiIndexLeafTarget target)
    {
        target = null!;
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var tableRootPage = FindRootPage(pager, header, "table", "target");
        if (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(tableRootPage)).PageType
            != SqliteBtreePageType.TableLeaf)
        {
            return false;
        }

        HashSet<long>? candidates = null;
        foreach (var indexName in MultiIndexNames)
        {
            var indexRootPage = FindRootPage(pager, header, "index", indexName);
            if (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRootPage)).PageType
                != SqliteBtreePageType.IndexInterior)
            {
                return false;
            }

            var root = SqliteIndexInteriorPageView.Parse(
                pager.ReadCommittedPage(indexRootPage),
                header.UsableSpace,
                header.TextEncoding);
            var indexCandidates = new HashSet<long>();
            var childPages = root.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(root.Header.RightMostChildPage)
                .ToArray();
            for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
            {
                var leaf = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(childPages[childIndex]),
                    header.UsableSpace,
                    header.TextEncoding);
                if (leaf.Cells.Count <= 1)
                    continue;

                var safeRecordCount = childIndex == root.Cells.Count
                    ? leaf.Cells.Count
                    : leaf.Cells.Count - 1;
                for (var recordIndex = 0; recordIndex < safeRecordCount; recordIndex++)
                {
                    var rowId = SqliteRecordCodec.Decode(leaf.GetRecord(recordIndex), header.TextEncoding)[^1]
                        .AsInteger();
                    indexCandidates.Add(rowId);
                }
            }

            if (indexCandidates.Count == 0)
                return false;

            candidates = candidates is null
                ? indexCandidates
                : candidates.Intersect(indexCandidates).ToHashSet();
            if (candidates.Count == 0)
                return false;
        }

        if (candidates is null || candidates.Count == 0)
            return false;

        target = new MultiIndexLeafTarget(candidates.Min(), rowCount);
        return true;
    }

    private static SingletonLeafTarget SeedSingletonReclamationTopology(
        IFileSystem fileSystem,
        string path)
    {
        var seeded = SeedDirectLeafTopology(fileSystem, path, minimumChildCount: 3);
        var topology = ReadTopology(fileSystem, path);
        topology.IndexRootCellCount.Should().Be(2);
        topology.Children.Should().HaveCount(3);

        using var store = SqlitePageStore.Open(fileSystem, path);
        var header = store.Header;
        var rootImage = store.ReadPage(topology.IndexRootPage);
        var root = SqliteIndexInteriorPageView.Parse(
            rootImage,
            header.UsableSpace,
            header.TextEncoding);
        var middlePage = root.Cells[1].Cell.LeftChildPage;
        var rightPage = root.Header.RightMostChildPage;
        var middle = SqliteIndexLeafPageView.Parse(
            store.ReadPage(middlePage),
            header.UsableSpace,
            header.TextEncoding);
        var right = SqliteIndexLeafPageView.Parse(
            store.ReadPage(rightPage),
            header.UsableSpace,
            header.TextEncoding);
        middle.Cells.Should().HaveCountGreaterThanOrEqualTo(2);
        right.Cells.Should().NotBeEmpty();

        // Reduce the middle leaf to a single key: its second key is promoted as
        // the separator that follows it and every later key moves to the right
        // leaf, ahead of the separator that used to sit between them. Ordering
        // is preserved, so the result is a valid tree whose middle child holds
        // exactly one key.
        var middleRecords = Enumerable.Range(0, middle.Cells.Count).Select(middle.GetRecord).ToArray();
        var rightRecords = Enumerable.Range(0, right.Cells.Count).Select(right.GetRecord).ToList();
        var transferredToRight = root.GetRecord(1);
        rightRecords.Insert(0, transferredToRight);
        rightRecords.InsertRange(0, middleRecords[2..]);
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);

        var replacementRoot = rootImage.ToArray();
        var rootBuilder = new SqliteIndexInteriorPageBuilder(
            header.PageSize,
            header.UsableSpace,
            rightPage,
            comparer);
        rootBuilder.Append(root.Cells[0].Cell, root.GetRecord(0));
        rootBuilder.Append(
            SqliteIndexInteriorCell.Create(middlePage, middleRecords[1], header.UsableSpace),
            middleRecords[1]);
        rootBuilder.WriteTo(replacementRoot);

        var replacementMiddle = BuildIndexLeafReplacement(
            header,
            store.ReadPage(middlePage),
            [middleRecords[0]]);
        var replacementRight = BuildIndexLeafReplacement(
            header,
            store.ReadPage(rightPage),
            rightRecords);
        store.WritePage(middlePage, replacementMiddle);
        store.WritePage(rightPage, replacementRight);
        store.WritePage(topology.IndexRootPage, replacementRoot);
        store.Flush();

        var rowId = SqliteRecordCodec.Decode(middleRecords[0], header.TextEncoding)[^1].AsInteger();
        return new SingletonLeafTarget(1, middlePage, rowId, seeded.RowCount);
    }

    private static byte[] BuildIndexLeafReplacement(
        SqliteDatabaseHeader header,
        ReadOnlySpan<byte> sourcePage,
        IEnumerable<byte[]> records)
    {
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var builder = new SqliteIndexLeafPageBuilder(header.PageSize, header.UsableSpace, comparer);
        foreach (var record in records)
            builder.Append(SqliteIndexLeafCell.Create(record, header.UsableSpace), record);

        var replacement = sourcePage.ToArray();
        builder.WriteTo(replacement);
        return replacement;
    }

    private static bool TryGetDirectLeafTarget(
        IndexTopology topology,
        int rowCount,
        int minimumChildCount,
        out SingletonLeafTarget target)
    {
        target = null!;
        if (topology.TableRootType != SqliteBtreePageType.TableLeaf
            || topology.IndexRootType != SqliteBtreePageType.IndexInterior
            || topology.Children.Count < minimumChildCount)
        {
            return false;
        }

        var candidate = topology.Children
            .SelectMany((child, childIndex) => child.RowIds.Select(rowId => new SingletonLeafTarget(
                childIndex,
                child.PageNumber,
                rowId,
                rowCount)))
            .FirstOrDefault(candidate =>
                topology.Children[candidate.ChildIndex].RowIds.Count > 1);
        if (candidate is null)
            return false;

        target = candidate;
        return true;
    }

    private static IndexTopology ReadTopology(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var tableRootPage = FindRootPage(pager, header, "table", "target");
        var indexRootPage = FindRootPage(pager, header, "index", IndexName);
        var tableRootType = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(tableRootPage)).PageType;
        var indexRootType = SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRootPage)).PageType;
        if (indexRootType != SqliteBtreePageType.IndexInterior)
        {
            return new IndexTopology(
                header,
                pager.CommittedPageCount,
                tableRootPage,
                tableRootType,
                indexRootPage,
                indexRootType,
                0,
                []);
        }

        var root = SqliteIndexInteriorPageView.Parse(
            pager.ReadCommittedPage(indexRootPage),
            header.UsableSpace,
            header.TextEncoding);
        var children = new List<IndexLeafTopology>();
        foreach (var childPage in root.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(root.Header.RightMostChildPage))
        {
            if (SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(childPage)).PageType
                != SqliteBtreePageType.IndexLeaf)
            {
                return new IndexTopology(
                    header,
                    pager.CommittedPageCount,
                    tableRootPage,
                    tableRootType,
                    indexRootPage,
                    indexRootType,
                    root.Cells.Count,
                    []);
            }

            var leaf = SqliteIndexLeafPageView.Parse(
                pager.ReadCommittedPage(childPage),
                header.UsableSpace,
                header.TextEncoding);
            children.Add(new IndexLeafTopology(
                childPage,
                Enumerable.Range(0, leaf.Cells.Count)
                    .Select(leaf.GetRecord)
                    .Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding)[^1].AsInteger())
                    .ToArray()));
        }

        return new IndexTopology(
            header,
            pager.CommittedPageCount,
            tableRootPage,
            tableRootType,
            indexRootPage,
            indexRootType,
            root.Cells.Count,
            children);
    }

    private static void AssertLeafDeletion(
        IndexTopology before,
        IndexTopology after,
        SingletonLeafTarget target)
    {
        after.PageCount.Should().Be(before.PageCount);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.TableRootType.Should().Be(SqliteBtreePageType.TableLeaf);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.IndexRootType.Should().Be(SqliteBtreePageType.IndexInterior);
        after.IndexRootCellCount.Should().Be(before.IndexRootCellCount);
        after.Header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.SchemaCookie.Should().Be(before.Header.SchemaCookie);
        after.Header.FirstFreelistTrunkPage.Should().Be(0);
        after.Header.FreelistPageCount.Should().Be(0);
        after.Children.Should().HaveCount(before.Children.Count);
        after.Children.Select(child => child.PageNumber).Should().Contain(target.PageNumber);
        after.Children.SelectMany(child => child.RowIds)
            .Should()
            .Equal(before.Children
                .SelectMany(child => child.RowIds)
                .Where(rowId => rowId != target.RowId));
    }

    private static void AssertReclaimedLeaf(
        IndexTopology before,
        IndexTopology after,
        SingletonLeafTarget target)
    {
        after.PageCount.Should().Be(before.PageCount);
        after.TableRootPage.Should().Be(before.TableRootPage);
        after.TableRootType.Should().Be(SqliteBtreePageType.TableLeaf);
        after.IndexRootPage.Should().Be(before.IndexRootPage);
        after.IndexRootType.Should().Be(SqliteBtreePageType.IndexInterior);
        after.IndexRootCellCount.Should().Be(before.IndexRootCellCount - 1);
        after.Header.DatabaseSizeInPages.Should().Be(before.Header.DatabaseSizeInPages);
        after.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
        after.Header.VersionValidFor.Should().Be(after.Header.ChangeCounter);
        after.Header.SchemaCookie.Should().Be(before.Header.SchemaCookie);
        var reclaimedPage = ReclaimedPage(before, after);
        after.Header.FirstFreelistTrunkPage.Should().Be(reclaimedPage);
        after.Header.FreelistPageCount.Should().Be(1);
        after.Children.Should().HaveCount(before.Children.Count - 1);
        after.Children.Select(child => child.PageNumber).Should().NotContain(reclaimedPage);
    }

    private static uint ReclaimedPage(IndexTopology before, IndexTopology after)
        => before.Children
            .Select(child => child.PageNumber)
            .Except(after.Children.Select(child => child.PageNumber))
            .Should()
            .ContainSingle()
            .Which;

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

    private static uint FindRootPage(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        string type,
        string name)
    {
        return checked((uint)ReadSchemaRecords(
                pager,
                header,
                pageNumber: 1,
                isFirstPage: true)
            .Single(values => values[0].AsText() == type && values[1].AsText() == name)[3]
            .AsInteger());
    }

    private static IEnumerable<SqlValue[]> ReadSchemaRecords(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        uint pageNumber,
        bool isFirstPage)
    {
        var page = pager.ReadCommittedPage(pageNumber);
        return SqliteBtreePageHeader.Parse(page, isFirstPage).PageType switch
        {
            SqliteBtreePageType.TableLeaf => SqliteTableLeafPageView.Parse(
                    page,
                    header.UsableSpace,
                    isFirstPage)
                .Cells
                .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding)),
            SqliteBtreePageType.TableInterior => SqliteTableInteriorPageView.Parse(
                    page,
                    header.UsableSpace,
                    isFirstPage)
                .Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(SqliteTableInteriorPageView.Parse(
                    page,
                    header.UsableSpace,
                    isFirstPage).Header.RightMostChildPage)
                .SelectMany(child => ReadSchemaRecords(pager, header, child, isFirstPage: false)),
            _ => throw new InvalidDataException("sqlite_schema has an unsupported b-tree page type."),
        };
    }

    private static void VerifyWithSqlite(string path, SingletonLeafTarget target)
    {
        var verificationPath = path + ".verify.db";
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

            using (var indexedCount = sqlite.CreateCommand())
            {
                indexedCount.CommandText = $"SELECT COUNT(*) FROM target INDEXED BY {IndexName};";
                Convert.ToInt64(indexedCount.ExecuteScalar()).Should().Be(target.RowCount - 1);
            }

            using var deleted = sqlite.CreateCommand();
            deleted.CommandText = $"SELECT COUNT(*) FROM target WHERE id = {target.RowId};";
            Convert.ToInt64(deleted.ExecuteScalar()).Should().Be(0);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static void VerifyMultiIndexWithSqlite(string path, MultiIndexLeafTarget target)
    {
        var verificationPath = path + ".verify.db";
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

            foreach (var indexName in MultiIndexNames)
            {
                using var indexedCount = sqlite.CreateCommand();
                indexedCount.CommandText = $"SELECT COUNT(*) FROM target INDEXED BY {indexName};";
                Convert.ToInt64(indexedCount.ExecuteScalar()).Should().Be(target.RowCount - 1);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(verificationPath);
        }
    }

    private static string RepeatedIndexColumns(int count) => string.Join(", ", Enumerable.Repeat("id", count));

    private static string InsertStatement(IEnumerable<int> rowIds)
        => $"INSERT INTO target VALUES {string.Join(", ", rowIds.Select(id => $"({id})"))};";

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Count(EmbeddedConnection connection)
    {
        using var statement = connection.Prepare("SELECT COUNT(*) FROM target;");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long CountById(EmbeddedConnection connection, long rowId)
    {
        using var statement = connection.Prepare($"SELECT COUNT(*) FROM target WHERE id = {rowId};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static long RowId(EmbeddedConnection connection, int rowId)
    {
        using var statement = connection.Prepare($"SELECT id FROM target WHERE id = {rowId};");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "managed-secondary-index-leaf-deletion-reclamation-tests");
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

    private static int SurvivingRowId(SingletonLeafTarget target)
        => target.RowId == 1 ? 2 : 1;

    private static long SurvivingRowId(long rowId) => rowId == 1 ? 2 : 1;

    private sealed record SingletonLeafTarget(int ChildIndex, uint PageNumber, long RowId, int RowCount);

    private sealed record MultiIndexLeafTarget(long RowId, int RowCount);

    private sealed record IndexLeafTopology(uint PageNumber, IReadOnlyList<long> RowIds);

    private sealed record IndexTopology(
        SqliteDatabaseHeader Header,
        uint PageCount,
        uint TableRootPage,
        SqliteBtreePageType TableRootType,
        uint IndexRootPage,
        SqliteBtreePageType IndexRootType,
        int IndexRootCellCount,
        IReadOnlyList<IndexLeafTopology> Children);
}
