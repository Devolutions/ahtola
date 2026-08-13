using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedIndexedBoundedLeafFastPathInvariantTests
{
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void CompatibleIndexedLeafMutationsWriteOnlyChangedTableIndexAndCatalogPages()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "indexed-bounded-leaf-scope.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            CreateCompatibleIndexedTarget(connection);
            Execute(connection, "CREATE TABLE untouched(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO untouched VALUES (1, 'stable');");

            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, "UPDATE target SET code = 'code-001-updated' WHERE id = 1;");

            // Three pages change - the table leaf, the target_code leaf and the
            // header page - and each is written once as a WAL frame and once by
            // the checkpoint. The WAL restart writes its next-generation header.
            // target_value is not written because the indexed column did not change.
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(7);

            writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            var duplicate = () => Execute(connection, "INSERT INTO target VALUES (99, 'code-002', 'duplicate');");
            duplicate.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
            faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);

            writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, "DELETE FROM target WHERE id = 3;");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(9);

            writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, "INSERT INTO target VALUES (30, 'code-030', 'inserted');");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(9);
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            pager.CommittedPageCount.Should().Be(5);
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(
                    FindRootPage(pager, header, "table", "target")))
                .PageType
                .Should()
                .Be(SqliteBtreePageType.TableLeaf);
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(
                    FindRootPage(pager, header, "index", "target_code")))
                .PageType
                .Should()
                .Be(SqliteBtreePageType.IndexLeaf);
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(
                    FindRootPage(pager, header, "index", "target_value")))
                .PageType
                .Should()
                .Be(SqliteBtreePageType.IndexLeaf);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT code FROM target WHERE id = 1;").Should().Be("code-001-updated");
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(20);
        Integer(reopenedConnection, "SELECT id FROM target WHERE code = 'code-030';").Should().Be(30);
        Integer(reopenedConnection, "SELECT id FROM target WHERE value = 'inserted';").Should().Be(30);
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target WHERE id = 3;").Should().Be(0);
        Text(reopenedConnection, "SELECT value FROM untouched WHERE id = 1;").Should().Be("stable");
    }

    [Test]
    public void IndexedLeafWalFailureRecoversTableAndIndexAsOnePriorCommit()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "indexed-bounded-leaf-wal-failure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            CreateCompatibleIndexedTarget(connection);

            // The mutation writes one frame per dirtied page - the table leaf,
            // the target_code leaf and the header page - so failing the last of
            // them aborts the transaction before it commits.
            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(() =>
                Execute(connection, "UPDATE target SET code = 'code-001-after' WHERE id = 1;"));
        }

        using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = recovered.Connect())
        {
            Text(connection, "SELECT code FROM target WHERE id = 1;").Should().Be("code-001");
            Integer(connection, "SELECT id FROM target WHERE code = 'code-001';").Should().Be(1);
            Integer(connection, "SELECT COUNT(*) FROM target WHERE code = 'code-001-after';").Should().Be(0);

            Execute(connection, "UPDATE target SET code = 'code-001-after' WHERE id = 1;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, "SELECT id FROM target WHERE code = 'code-001-after';").Should().Be(1);
    }

    [Test]
    public void EncryptedCompatibleIndexedLeafMutationReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-indexed-bounded-leaf.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            CreateCompatibleIndexedTarget(connection);
            Execute(connection, "UPDATE target SET code = 'code-001-encrypted' WHERE id = 1;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, "SELECT id FROM target WHERE code = 'code-001-encrypted';").Should().Be(1);
    }

    [Test]
    public void CompatibleIndexedLeafMutationCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "indexed-bounded-leaf-read-only.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            CreateCompatibleIndexedTarget(connection);

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(connection, "UPDATE target SET code = 'code-001-read-only' WHERE id = 1;"))!
                .Message.Should().Be("attempt to write a readonly database");
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT code FROM target WHERE id = 1;").Should().Be("code-001");
    }

    [Test]
    public void CorruptIndexedLeafIsRejectedBeforeAnyMutationWrite()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "indexed-bounded-leaf-corruption.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            CreateCompatibleIndexedTarget(connection);

        uint indexRootPage;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            indexRootPage = FindRootPage(pager, header, "index", "target_code");
        }

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(indexRootPage);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(indexRootPage, page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void IndexedInteriorRootMutatesIncrementallyInsteadOfRewritingEveryPage()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "indexed-bounded-leaf-interior-fallback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildWideTargetInsert(1, 96));
            Execute(connection, "CREATE INDEX target_value ON target(value);");
        }

        uint pageCountBefore;
        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            pageCountBefore = pager.CommittedPageCount;
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(
                    FindRootPage(pager, header, "index", "target_value")))
                .PageType
                .Should()
                .Be(SqliteBtreePageType.IndexInterior);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, "UPDATE target SET value = 'replacement-value' WHERE id = 1;");

            // An index whose root is interior no longer forces a rewrite of
            // every page: the cursor descends to the one index leaf that holds
            // the old key and writes only the pages it dirties.
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().BeLessThanOrEqualTo(8);
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            pager.CommittedPageCount.Should().Be(pageCountBefore);

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT value FROM target WHERE id = 1;").Should().Be("replacement-value");
        Integer(reopenedConnection, "SELECT id FROM target WHERE value = 'replacement-value';").Should().Be(1);
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(96);
    }

    private static void CreateCompatibleIndexedTarget(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT, value TEXT);");
        Execute(connection, BuildTargetInsert(1, 20));
        Execute(connection, "CREATE UNIQUE INDEX target_code ON target(code);");
        Execute(connection, "CREATE INDEX target_value ON target(value);");
    }

    private static string BuildTargetInsert(int firstId, int count)
    {
        var rows = Enumerable.Range(firstId, count)
            .Select(id => $"({id}, 'code-{id:D3}', 'value-{id:D3}')");
        return $"INSERT INTO target VALUES {string.Join(", ", rows)};";
    }

    private static string BuildWideTargetInsert(int firstId, int count)
    {
        var suffix = new string('x', 72);
        var rows = Enumerable.Range(firstId, count)
            .Select(id => $"({id}, 'value-{id:D3}-{suffix}')");
        return $"INSERT INTO target VALUES {string.Join(", ", rows)};";
    }

    private static uint FindRootPage(
        SqlitePager pager,
        SqliteDatabaseHeader header,
        string type,
        string name)
    {
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == type && values[1].AsText() == name)[3]
            .AsInteger());
    }

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
}
