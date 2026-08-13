using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedNestedSecondaryIndexLeafMutationTests
{
    private const int PageSize = SqlitePageSize.Minimum;
    private const int InsertedId = 10;

    [Test]
    public void NestedIndexLeafInsertionPersistsReopensAndPassesExternalSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath();
        try
        {
            var topology = CreatePreparedTopology(path, PhysicalFileSystem.Instance);
            byte[] rootBefore;
            byte[] parentBefore;
            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                rootBefore = pager.ReadCommittedPage(topology.IndexRootPage);
                parentBefore = pager.ReadCommittedPage(topology.TargetParentPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, $"INSERT INTO t VALUES ({InsertedId}, '{ValueFor(InsertedId)}');");

            using (var pager = SqlitePager.Open(
                       PhysicalFileSystem.Instance,
                       path,
                       path + "-wal",
                       readOnly: true))
            {
                var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
                pager.ReadCommittedPage(topology.IndexRootPage).Should().Equal(rootBefore);
                pager.ReadCommittedPage(topology.TargetParentPage).Should().Equal(parentBefore);
                var targetLeaf = SqliteIndexLeafPageView.Parse(
                    pager.ReadCommittedPage(topology.TargetLeafPage),
                    header.UsableSpace,
                    header.TextEncoding);
                targetLeaf.Cells
                    .Select((_, index) => targetLeaf.GetRecord(index))
                    .Select(record => SqliteRecordCodec.Decode(record, header.TextEncoding)[1].AsInteger())
                    .Should()
                    .Equal(InsertedId, 11);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path, readOnly: true))
            using (var connection = reopened.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM t;").Should().Be(18);
                Scalar(connection, $"SELECT id FROM t WHERE value = '{ValueFor(InsertedId)}';")
                    .Should()
                    .Be(InsertedId);
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                using (var integrity = sqlite.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check;";
                    integrity.ExecuteScalar().Should().Be("ok");
                }

                using var indexed = sqlite.CreateCommand();
                indexed.CommandText =
                    $"SELECT id FROM t INDEXED BY t_value_binary WHERE value = '{ValueFor(InsertedId)}';";
                Convert.ToInt64(indexed.ExecuteScalar()).Should().Be(InsertedId);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EveryNestedIndexLeafWalFrameInterruptionRetainsThePriorCommittedTopology()
    {
        for (var failedFrame = 1; failedFrame <= 3; failedFrame++)
        {
            var faults = new DeterministicFaultInjector();
            var fileSystem = new InMemoryFileSystem(faults);
            var path = $"nested-index-leaf-insertion-{failedFrame}.db";
            var topology = CreatePreparedTopology(path, fileSystem);
            byte[] rootBefore;
            byte[] parentBefore;
            using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            {
                rootBefore = pager.ReadCommittedPage(topology.IndexRootPage);
                parentBefore = pager.ReadCommittedPage(topology.TargetParentPage);
            }

            using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = database.Connect())
            {
                faults.FailOnOccurrence(
                    FileSystemOperation.Write,
                    faults.GetOperationCount(FileSystemOperation.Write) + failedFrame);
                Assert.Throws<IOException>(() =>
                    Execute(connection, $"INSERT INTO t VALUES ({InsertedId}, '{ValueFor(InsertedId)}');"));
            }

            using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
            using (var connection = recovered.Connect())
            {
                Scalar(connection, "SELECT COUNT(*) FROM t;").Should().Be(17);
                Scalar(connection, $"SELECT COUNT(*) FROM t WHERE id = {InsertedId};").Should().Be(0);
            }

            using var committed = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
            committed.ReadCommittedPage(topology.IndexRootPage).Should().Equal(rootBefore);
            committed.ReadCommittedPage(topology.TargetParentPage).Should().Equal(parentBefore);
            var header = SqliteDatabaseHeader.Parse(committed.ReadCommittedPage(1));
            var targetLeaf = SqliteIndexLeafPageView.Parse(
                committed.ReadCommittedPage(topology.TargetLeafPage),
                header.UsableSpace,
                header.TextEncoding);
            targetLeaf.Search(BuildIndexRecord(11, header.TextEncoding)).IsExact.Should().BeTrue();
            targetLeaf.Search(BuildIndexRecord(InsertedId, header.TextEncoding)).IsExact.Should().BeFalse();
        }
    }

    private static NestedIndexTopology CreatePreparedTopology(string path, IFileSystem fileSystem)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = PageSize };
        using (SqlitePager.Create(
                   fileSystem,
                   path,
                   path + "-wal",
                   SqliteWalHeader.Create(PageSize, salt1: 0x1234_5678, salt2: 0x9ABC_DEF0),
                   header))
        {
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildInitialInsert());
            Execute(connection, "CREATE INDEX t_value_binary ON t(value);");
        }

        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: false);
        var sourceHeader = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var rootPage = FindIndexRootPage(pager.ReadCommittedPage(1), sourceHeader);
        var sourceRoot = SqliteIndexLeafPageView.Parse(
            pager.ReadCommittedPage(rootPage),
            sourceHeader.UsableSpace,
            sourceHeader.TextEncoding);
        var records = sourceRoot.Cells.Select((_, index) => sourceRoot.GetRecord(index)).ToArray();
        records.Length.Should().Be(17);

        var nextPage = checked(sourceHeader.DatabaseSizeInPages + 1);
        var leaves = new List<NestedIndexLeaf>(8);
        var recordOffset = 0;
        foreach (var groupSize in new[] { 2, 2, 2, 3, 2, 2, 2, 2 })
        {
            leaves.Add(new NestedIndexLeaf(
                nextPage++,
                records.Skip(recordOffset).Take(groupSize).Select(record => record.ToArray()).ToList()));
            recordOffset += groupSize;
        }

        recordOffset.Should().Be(records.Length);
        var leftParent = BuildInteriorPage(
            nextPage++,
            leaves.Take(4).ToArray(),
            sourceHeader);
        var rightParent = BuildInteriorPage(
            nextPage++,
            leaves.Skip(4).Take(4).ToArray(),
            sourceHeader);
        var rootSeparator = leaves[3].Records[^1];
        leaves[3].Records.RemoveAt(leaves[3].Records.Count - 1);

        var rootBuilder = new SqliteIndexInteriorPageBuilder(
            sourceHeader.PageSize,
            sourceHeader.UsableSpace,
            rightParent.PageNumber,
            new SqliteIndexRecordComparer(sourceHeader.TextEncoding));
        rootBuilder.Append(
            SqliteIndexInteriorCell.Create(leftParent.PageNumber, rootSeparator, sourceHeader.UsableSpace),
            rootSeparator);
        var replacementRoot = pager.ReadCommittedPage(rootPage);
        rootBuilder.WriteTo(replacementRoot);

        var targetPageCount = checked(nextPage - 1);
        var replacementSchemaPage = pager.ReadCommittedPage(1);
        var updatedHeader = sourceHeader with
        {
            ChangeCounter = sourceHeader.ChangeCounter + 1,
            DatabaseSizeInPages = targetPageCount,
            VersionValidFor = sourceHeader.ChangeCounter + 1,
        };
        updatedHeader.WriteTo(replacementSchemaPage);

        using (var transaction = pager.BeginTransaction(targetPageCount))
        {
            foreach (var leaf in leaves)
                transaction.WritePage(leaf.PageNumber, BuildLeafPage(leaf.Records, sourceHeader));
            transaction.WritePage(leftParent.PageNumber, leftParent.Page);
            transaction.WritePage(rightParent.PageNumber, rightParent.Page);
            transaction.WritePage(rootPage, replacementRoot);
            transaction.WritePage(1, replacementSchemaPage);
            transaction.Commit();
        }

        return new NestedIndexTopology(rootPage, rightParent.PageNumber, leaves[4].PageNumber);
    }

    private static NestedIndexParent BuildInteriorPage(
        uint pageNumber,
        IReadOnlyList<NestedIndexLeaf> children,
        SqliteDatabaseHeader header)
    {
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var builder = new SqliteIndexInteriorPageBuilder(
            header.PageSize,
            header.UsableSpace,
            children[^1].PageNumber,
            comparer);
        for (var childIndex = 0; childIndex < children.Count - 1; childIndex++)
        {
            var child = children[childIndex];
            var separator = child.Records[^1];
            child.Records.RemoveAt(child.Records.Count - 1);
            builder.Append(
                SqliteIndexInteriorCell.Create(child.PageNumber, separator, header.UsableSpace),
                separator);
        }

        return new NestedIndexParent(pageNumber, builder.Build());
    }

    private static byte[] BuildLeafPage(IReadOnlyList<byte[]> records, SqliteDatabaseHeader header)
    {
        var comparer = new SqliteIndexRecordComparer(header.TextEncoding);
        var builder = new SqliteIndexLeafPageBuilder(header.PageSize, header.UsableSpace, comparer);
        foreach (var record in records)
            builder.Append(SqliteIndexLeafCell.Create(record, header.UsableSpace), record);
        return builder.Build();
    }

    private static uint FindIndexRootPage(ReadOnlySpan<byte> schemaPage, SqliteDatabaseHeader header)
    {
        var schema = SqliteTableLeafPageView.Parse(schemaPage, header.UsableSpace, isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "index" && values[1].AsText() == "t_value_binary")[3]
            .AsInteger());
    }

    private static byte[] BuildIndexRecord(int id, SqliteTextEncoding textEncoding)
        => SqliteRecordCodec.Encode(
            [SqlValue.Text(ValueFor(id)), SqlValue.Integer(id)],
            textEncoding);

    private static string BuildInitialInsert()
        => $"INSERT INTO t VALUES {string.Join(", ", Enumerable.Range(1, 18)
            .Where(id => id != InsertedId)
            .Select(id => $"({id}, '{ValueFor(id)}')"))};";

    private static string ValueFor(int id) => $"value-{id:D3}";

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static long Scalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsInteger();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-nested-secondary-index-leaf-mutation-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
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

    private sealed record NestedIndexLeaf(uint PageNumber, List<byte[]> Records);

    private sealed record NestedIndexParent(uint PageNumber, byte[] Page);

    private sealed record NestedIndexTopology(uint IndexRootPage, uint TargetParentPage, uint TargetLeafPage);
}
