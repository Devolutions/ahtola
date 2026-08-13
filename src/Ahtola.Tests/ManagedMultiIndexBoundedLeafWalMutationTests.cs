using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedMultiIndexBoundedLeafWalMutationTests
{
    private const string EncryptionKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private static readonly string[] IndexNames = ["target_code", "target_category_code", "target_note"];

    [Test]
    public void ThreeDistinctIndexLeavesCommitBoundedCrudUnderPressureAndReopen()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-bounded-crud.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var writableConnection = database.Connect())
        {
            SeedThreeIndexTarget(writableConnection);
            var before = ReadRoots(fileSystem, path);
            AssertLeafTopology(fileSystem, path, before);

            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(writableConnection,
                $"UPDATE target SET code = '{ChangedCode(12)}', category = '{ChangedCategory(12)}', note = '{ChangedNote(12)}' WHERE id = 12;");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(11);

            var afterUpdate = ReadRoots(fileSystem, path);
            afterUpdate.Header.ChangeCounter.Should().Be(before.Header.ChangeCounter + 1);
            afterUpdate.Header.VersionValidFor.Should().Be(afterUpdate.Header.ChangeCounter);
            AssertLeafTopology(fileSystem, path, afterUpdate);

            writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(writableConnection,
                    $"INSERT INTO target VALUES (99, '{Code(2)}', '{Category(99)}', '{Note(99)}');"));
            faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);

            writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(writableConnection, "DELETE FROM target WHERE id = 3;");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(11);

            writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(writableConnection,
                $"INSERT INTO target VALUES (30, '{Code(30)}', '{Category(30)}', '{Note(30)}');");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().Be(11);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT code FROM target WHERE id = 12;").Should().Be(ChangedCode(12));
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target;").Should().Be(24);
        Integer(reopenedConnection, $"SELECT id FROM target WHERE code = '{Code(30)}';").Should().Be(30);
        Integer(reopenedConnection, $"SELECT id FROM target WHERE category = '{Category(30)}' AND code = '{Code(30)}';").Should().Be(30);
        Integer(reopenedConnection, $"SELECT id FROM target WHERE note = '{Note(30)}';").Should().Be(30);
        Integer(reopenedConnection, "SELECT COUNT(*) FROM target WHERE id = 3;").Should().Be(0);
    }

    [Test]
    public void ThreeIndexLeafWalCommitFailureRecoversThePriorCatalog()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-bounded-wal-failure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var writableConnection = database.Connect())
        {
            SeedThreeIndexTarget(writableConnection);
            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 5);

            Assert.Throws<IOException>(() =>
                Execute(writableConnection,
                    $"UPDATE target SET code = '{ChangedCode(12)}', category = '{ChangedCategory(12)}', note = '{ChangedNote(12)}' WHERE id = 12;"));
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        Text(recoveredConnection, "SELECT code FROM target WHERE id = 12;").Should().Be(Code(12));
        Integer(recoveredConnection, $"SELECT id FROM target WHERE code = '{Code(12)}';").Should().Be(12);
        Integer(recoveredConnection, $"SELECT id FROM target WHERE category = '{Category(12)}' AND code = '{Code(12)}';").Should().Be(12);
        Integer(recoveredConnection, $"SELECT id FROM target WHERE note = '{Note(12)}';").Should().Be(12);
        Integer(recoveredConnection, $"SELECT COUNT(*) FROM target WHERE code = '{ChangedCode(12)}';").Should().Be(0);
    }

    [Test]
    public void EncryptedThreeIndexLeafMutationReopensReadOnly()
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            AhtolaEncryptionCipher.Aes256Gcm,
            EncryptionKey);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        const string path = "encrypted-multi-index-bounded.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var writableConnection = database.Connect())
        {
            SeedThreeIndexTarget(writableConnection);
            Execute(writableConnection,
                $"UPDATE target SET code = '{ChangedCode(12)}', category = '{ChangedCategory(12)}', note = '{ChangedNote(12)}' WHERE id = 12;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        Integer(reopenedConnection, $"SELECT id FROM target WHERE code = '{ChangedCode(12)}';").Should().Be(12);
        Integer(reopenedConnection,
            $"SELECT id FROM target WHERE category = '{ChangedCategory(12)}' AND code = '{ChangedCode(12)}';").Should().Be(12);
        Integer(reopenedConnection, $"SELECT id FROM target WHERE note = '{ChangedNote(12)}';").Should().Be(12);
    }

    [Test]
    public void ThreeIndexLeafMutationCannotBypassReadOnlyPager()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-bounded-read-only.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var writableConnection = database.Connect())
            SeedThreeIndexTarget(writableConnection);

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        using (var readOnly = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var readOnlyConnection = readOnly.Connect())
        {
            Assert.Throws<EmbeddedSqlException>(() =>
                Execute(readOnlyConnection,
                    $"UPDATE target SET code = '{ChangedCode(12)}', category = '{ChangedCategory(12)}', note = '{ChangedNote(12)}' WHERE id = 12;"));
        }

        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT code FROM target WHERE id = 12;").Should().Be(Code(12));
    }

    [Test]
    public void CorruptThirdIndexLeafIsRejectedBeforeAnyManagedMutationWrite()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-bounded-corruption.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
            SeedThreeIndexTarget(connection);

        var roots = ReadRoots(fileSystem, path);
        using (var store = SqlitePageStore.Open(fileSystem, path))
        {
            var page = store.ReadPage(roots.IndexRoots[^1]);
            page[0] = (byte)SqliteBtreePageType.TableLeaf;
            store.WritePage(roots.IndexRoots[^1], page);
            store.Flush();
        }

        var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
        faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBefore);
    }

    [Test]
    public void UnsupportedSecondaryIndexRootFallsBackBeforeAnyBoundedLeafCommit()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "multi-index-bounded-index-fallback.db";
        CreateMinimumPageDatabase(fileSystem, path);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT);");
            Execute(connection, BuildFallbackInsert());
            Execute(connection, "CREATE INDEX target_code ON target(code);");
            Execute(connection, "CREATE INDEX target_code_twice ON target(code, code);");
        }

        using (var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true))
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(FindRootPage(pager, header, "table", "target")))
                .PageType.Should().Be(SqliteBtreePageType.TableLeaf);
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(FindRootPage(pager, header, "index", "target_code")))
                .PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(FindRootPage(pager, header, "index", "target_code_twice")))
                .PageType.Should().Be(SqliteBtreePageType.IndexInterior);
        }

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            var writesBefore = faults.GetOperationCount(FileSystemOperation.Write);
            Execute(connection, $"UPDATE target SET code = '{FallbackChangedCode(1)}' WHERE id = 1;");
            (faults.GetOperationCount(FileSystemOperation.Write) - writesBefore).Should().BeGreaterThan(8);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Text(reopenedConnection, "SELECT code FROM target WHERE id = 1;").Should().Be(FallbackChangedCode(1));
        Integer(reopenedConnection, $"SELECT id FROM target WHERE code = '{FallbackChangedCode(1)}';").Should().Be(1);
    }

    private static void SeedThreeIndexTarget(EmbeddedConnection connection)
    {
        Execute(connection, "CREATE TABLE target(id INTEGER PRIMARY KEY, code TEXT, category TEXT, note TEXT);");
        Execute(connection, BuildInsert(1, 24));
        Execute(connection, "CREATE UNIQUE INDEX target_code ON target(code);");
        Execute(connection, "CREATE INDEX target_category_code ON target(category, code);");
        Execute(connection, "CREATE INDEX target_note ON target(note);");
    }

    private static void CreateMinimumPageDatabase(IFileSystem fileSystem, string path)
    {
        var header = SqliteDatabaseHeader.CreateDefault() with { PageSize = SqlitePageSize.Minimum };
        using var pager = SqlitePager.Create(
            fileSystem,
            path,
            path + "-wal",
            SqliteWalHeader.Create(SqlitePageSize.Minimum, salt1: 0x1020_3040, salt2: 0x5060_7080),
            header);
    }

    private static RootSnapshot ReadRoots(IFileSystem fileSystem, string path)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(1));
        return new RootSnapshot(
            header,
            FindRootPage(pager, header, "table", "target"),
            IndexNames.Select(name => FindRootPage(pager, header, "index", name)).ToArray());
    }

    private static void AssertLeafTopology(IFileSystem fileSystem, string path, RootSnapshot roots)
    {
        using var pager = SqlitePager.Open(fileSystem, path, path + "-wal", readOnly: true);
        roots.IndexRoots.Append(roots.TableRoot).Should().OnlyHaveUniqueItems();
        SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(roots.TableRoot))
            .PageType.Should().Be(SqliteBtreePageType.TableLeaf);
        foreach (var indexRoot in roots.IndexRoots)
        {
            SqliteBtreePageHeader.Parse(pager.ReadCommittedPage(indexRoot))
                .PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
        }
    }

    private static uint FindRootPage(SqlitePager pager, SqliteDatabaseHeader header, string type, string name)
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

    private static string BuildInsert(int firstId, int count)
        => $"INSERT INTO target VALUES {string.Join(", ", Enumerable.Range(firstId, count)
            .Select(id => $"({id}, '{Code(id)}', '{Category(id)}', '{Note(id)}')"))};";

    private static string BuildFallbackInsert()
        => $"INSERT INTO target VALUES {string.Join(", ", Enumerable.Range(1, 6)
            .Select(id => $"({id}, '{FallbackCode(id)}')"))};";

    private static string Code(int id) => $"code-{id:D3}-{new string('c', 48)}";

    private static string ChangedCode(int id) => $"new-{id:D3}-{new string('d', 49)}";

    private static string Category(int id) => $"category-{id % 5:D2}-{new string('g', 18)}";

    private static string ChangedCategory(int id) => $"bucket-{id % 5:D2}-{new string('h', 20)}";

    private static string Note(int id) => $"note-{id:D3}-{new string('n', 48)}";

    private static string ChangedNote(int id) => $"memo-{id:D3}-{new string('m', 48)}";

    private static string FallbackCode(int id) => $"code-{id:D3}-{new string('x', 35)}";

    private static string FallbackChangedCode(int id) => $"next-{id:D3}-{new string('z', 35)}";

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

    private sealed record RootSnapshot(
        SqliteDatabaseHeader Header,
        uint TableRoot,
        IReadOnlyList<uint> IndexRoots);
}
