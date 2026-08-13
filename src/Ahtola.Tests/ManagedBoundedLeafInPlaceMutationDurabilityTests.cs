using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedBoundedLeafInPlaceMutationDurabilityTests
{
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void BoundedLeafUpdateUnderPressureWritesOnlyLeafAndHeaderAndReopens()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "bounded-leaf-pressure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildTargetInsert());

            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, "UPDATE target SET value = 'after' WHERE id = 48;");

            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(5);
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
            pager.CommittedPageCount.Should().Be(2);

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT value FROM target WHERE id = 48;").Should().Be("after");
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(96);
    }

    [Test]
    public void BoundedLeafWalFailureRecoversPriorCommittedValue()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "bounded-leaf-wal-failure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO target VALUES (1, 'before');");

            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 2);
            Assert.Throws<IOException>(() => Execute(connection, "UPDATE target SET value = 'after' WHERE id = 1;"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT value FROM target WHERE id = 1;").Should().Be("before");
    }

    [Test]
    public void EncryptedBoundedLeafUpdateReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-bounded-leaf.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO target VALUES (1, 'before');");
            Execute(connection, "UPDATE target SET value = 'after' WHERE id = 1;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT value FROM target WHERE id = 1;").Should().Be("after");
    }

    [Test]
    public void BoundedLeafMutationDoesNotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "bounded-leaf-read-only.db";
        SeedTarget(fileSystem, path);
        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);

        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(connection, "UPDATE target SET value = 'after' WHERE id = 1;"))!
                .Message.Should().Be("attempt to write a readonly database");
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT value FROM target WHERE id = 1;").Should().Be("before");
    }

    [Test]
    public void CorruptBoundedLeafIsRejectedBeforeWritingAnyMutationPages()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "bounded-leaf-corruption.db";
        SeedTarget(fileSystem, path);
        var rootPage = FindTableRootPage(fileSystem, path, "target");

        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(rootPage);
            page[0] = (byte)SqliteBtreePageType.IndexLeaf;
            store.WritePage(rootPage, page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    private static void SeedTarget(IFileSystem fileSystem, string path)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO target VALUES (1, 'before');");
    }

    private static uint FindTableRootPage(IFileSystem fileSystem, string path, string tableName)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        var schema = SqliteTableLeafPageView.Parse(
            pager.ReadCommittedPage(1),
            header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding))
            .Single(values => values[0].AsText() == "table" && values[1].AsText() == tableName)[3]
            .AsInteger());
    }

    private static string BuildTargetInsert()
    {
        var rows = Enumerable.Range(1, 96)
            .Select(id => $"({id}, 'value-{id:D4}-{new string('p', 16)}')");
        return $"INSERT INTO target VALUES {string.Join(", ", rows)};";
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
