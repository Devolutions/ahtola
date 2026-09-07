using Ahtola.Core;
using Ahtola.Core.Storage;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Verifies the PAGE workstream's first vertical slice: physical open of a managed file
/// database reconstructs only the schema catalog, and a base table's committed rows are read
/// from its b-tree at most once, the first time anything actually observes
/// <c>EmbeddedTable.Rows</c>/<c>RowIds</c> (see <c>EmbeddedTable.AttachPendingRowLoader</c> and
/// <c>EmbeddedFileStore.Load</c>). A table with a registered secondary/PK index is still
/// eagerly validated (and thus hydrated) during <c>Load()</c>, because index content/uniqueness
/// cross-validation is not deferred by this slice — that boundary is asserted explicitly below
/// rather than silently assumed.
/// </summary>
[NonParallelizable]
public sealed class PageBackedLazyRowLoadTests
{
    [Test]
    public void PhysicalOpenDoesNotMaterializeIndexlessRowidTableRows()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-rowid.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE big(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE small(id INTEGER PRIMARY KEY, value TEXT);
                """);
            foreach (var i in Enumerable.Range(0, 200))
                Execute(connection, $"INSERT INTO big VALUES ({i}, 'row-{i}');");
            Execute(connection, "INSERT INTO small VALUES (1, 'only-row');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var catalog = reopened.LiveCatalog;
        catalog.Tables["big"].HasPendingRowLoad.Should().BeTrue(
            "physical open must not walk an indexless base table's b-tree eagerly");
        catalog.Tables["small"].HasPendingRowLoad.Should().BeTrue(
            "physical open must not walk an indexless base table's b-tree eagerly");

        using var connectionAfterOpen = reopened.Connect();
        var bigCount = ReadInteger(connectionAfterOpen, "SELECT COUNT(*) FROM big;");
        bigCount.Should().Be(200);

        catalog.Tables["big"].HasPendingRowLoad.Should().BeFalse(
            "scanning 'big' must hydrate it");
        catalog.Tables["small"].HasPendingRowLoad.Should().BeTrue(
            "scanning 'big' must not force-load the untouched 'small' table");

        ReadText(connectionAfterOpen, "SELECT value FROM small WHERE id = 1;").Should().Be("only-row");
        catalog.Tables["small"].HasPendingRowLoad.Should().BeFalse(
            "'small' hydrates once actually scanned");
    }

    [Test]
    public void PhysicalOpenDoesNotMaterializeWithoutRowidTableRows()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-without-rowid.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE wr(k TEXT PRIMARY KEY, v INTEGER) WITHOUT ROWID;");
            foreach (var i in Enumerable.Range(0, 50))
                Execute(connection, $"INSERT INTO wr VALUES ('key-{i}', {i});");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var catalog = reopened.LiveCatalog;
        catalog.Tables["wr"].HasPendingRowLoad.Should().BeTrue(
            "physical open must not walk a WITHOUT ROWID base table's b-tree eagerly");

        using var connection2 = reopened.Connect();
        ReadInteger(connection2, "SELECT v FROM wr WHERE k = 'key-7';").Should().Be(7);
        catalog.Tables["wr"].HasPendingRowLoad.Should().BeFalse();
    }

    [Test]
    public void PhysicalOpenEagerlyValidatesAnIndexedTableAgainstItsIndex()
    {
        // Documents the known, deliberate boundary of this slice: ValidateStoredIndex
        // cross-checks a stored index's content against its owning table (BuildIndexRecords),
        // and that content/uniqueness validation is not deferred, so an indexed table is still
        // hydrated during Load() exactly as before this change.
        var fileSystem = new InMemoryFileSystem();
        const string path = "eager-indexed.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE indexed(id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX indexed_value ON indexed(value);
                """);
            Execute(connection, "INSERT INTO indexed VALUES (1, 'a');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        reopened.LiveCatalog.Tables["indexed"].HasPendingRowLoad.Should().BeFalse(
            "a table with a registered index is still validated (and hydrated) eagerly by Load()");
    }

    [Test]
    public void ReadOnlyStatementsOnOneTableDoNotForceLoadAnUntouchedSibling()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-read-only.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE touched(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE untouched(id INTEGER PRIMARY KEY, value TEXT);
                """);
            Execute(connection, "INSERT INTO touched VALUES (1, 'a'), (2, 'b');");
            Execute(connection, "INSERT INTO untouched VALUES (1, 'z');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var catalog = reopened.LiveCatalog;
        using var connection2 = reopened.Connect();

        // A read-only statement never commits a catalog write, so it must not force-load a
        // table it never references, no matter how many times it runs.
        ReadRows(connection2, "SELECT id, value FROM touched ORDER BY id;")
            .Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should()
            .Equal((1L, "a"), (2L, "b"));
        ReadInteger(connection2, "SELECT COUNT(*) FROM touched;").Should().Be(2);

        catalog.Tables["untouched"].HasPendingRowLoad.Should().BeTrue(
            "read-only statements against 'touched' must not force-load the untouched sibling table");

        ReadText(connection2, "SELECT value FROM untouched WHERE id = 1;").Should().Be("z");
        catalog.Tables["untouched"].HasPendingRowLoad.Should().BeFalse();
    }

    [Test]
    public void CommittingAWriteEagerlyValidatesEveryOtherTableIncludingUntouchedSiblings()
    {
        // Documents a second, separate, and known boundary of this slice: PersistCore's
        // ValidateTableRepresentable pass still runs unconditionally over every table in the
        // catalog on every committed write (pre-existing behavior, unchanged here), so a write
        // to 'touched' still hydrates the untouched 'sibling' table once the statement commits.
        // Making that pass skip a provably row-storage-unchanged table (the existing
        // IsTableRowStorageUnchangedFromPrevious machinery) without forcing it to hydrate first
        // is tracked as separate follow-up work, not silently claimed here.
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-mutation.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE touched(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE sibling(id INTEGER PRIMARY KEY, value TEXT);
                """);
            Execute(connection, "INSERT INTO touched VALUES (1, 'a');");
            Execute(connection, "INSERT INTO sibling VALUES (1, 'z');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var catalog = reopened.LiveCatalog;
        using var connection2 = reopened.Connect();

        catalog.Tables["sibling"].HasPendingRowLoad.Should().BeTrue();
        Execute(connection2, "INSERT INTO touched VALUES (2, 'b');");
        catalog.Tables["sibling"].HasPendingRowLoad.Should().BeFalse(
            "a committed write still validates (and thus hydrates) every table today");

        ReadRows(connection2, "SELECT id, value FROM touched ORDER BY id;")
            .Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should()
            .Equal((1L, "a"), (2L, "b"));
        ReadText(connection2, "SELECT value FROM sibling WHERE id = 1;").Should().Be("z");
    }

    [Test]
    public void RollingBackAStatementLeavesAnUntouchedLazyTableCorrectOnReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-rollback.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE mutated(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE bystander(id INTEGER PRIMARY KEY, value TEXT);
                """);
            Execute(connection, "INSERT INTO mutated VALUES (1, 'a');");
            Execute(connection, "INSERT INTO bystander VALUES (1, 'z');");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection2 = reopened.Connect())
        {
            var catalog = reopened.LiveCatalog;
            catalog.Tables["bystander"].HasPendingRowLoad.Should().BeTrue();

            Execute(
                connection2,
                """
                BEGIN;
                INSERT INTO mutated VALUES (2, 'b');
                ROLLBACK;
                """);

            // A rolled-back statement's working catalog clone is discarded entirely; the
            // published table for 'bystander' must never have been touched by it.
            catalog.Tables["bystander"].HasPendingRowLoad.Should().BeTrue(
                "a rolled-back transaction touching only 'mutated' must not have loaded 'bystander'");

            ReadInteger(connection2, "SELECT COUNT(*) FROM mutated;").Should().Be(1);
        }

        using (var verifier = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true))
        using (var connection3 = verifier.Connect())
        {
            ReadInteger(connection3, "SELECT COUNT(*) FROM mutated;").Should().Be(1);
            ReadText(connection3, "SELECT value FROM bystander WHERE id = 1;").Should().Be("z");
        }
    }

    [Test]
    public void EnablingMvccHydratesAStillPendingReopenedTableSoAConcurrentReaderStaysIsolated()
    {
        // Regression coverage for the fix in EmbeddedDatabase.PublishCatalog /
        // EstablishHeapBaselineForMvccLocked: MVCC's per-transaction merge
        // (MergeConcurrentCatalogFromStoreLocked) assumes the heap catalog (EmbeddedTable.Rows)
        // is a stable baseline the version-chain overlay tracks changes relative to. A page-backed
        // table that is still lazily pending when a reader begins a concurrent transaction must
        // not silently materialize from whatever a peer commits *after* that reader began —
        // exactly the scenario a physically reopened (never-yet-touched) table exercises.
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-mvcc-isolation.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(v INTEGER);");
            Execute(connection, "INSERT INTO t VALUES (1);");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        reopened.LiveCatalog.Tables["t"].HasPendingRowLoad.Should().BeTrue(
            "a reopened, indexless table must still be lazy immediately after physical open");

        using var writer = reopened.Connect();
        using var reader = reopened.Connect();
        Execute(writer, "PRAGMA journal_mode=mvcc;");
        reopened.LiveCatalog.Tables["t"].HasPendingRowLoad.Should().BeFalse(
            "enabling MVCC must establish the heap baseline for every table up front");

        Execute(reader, "BEGIN CONCURRENT;");
        Execute(writer, "BEGIN CONCURRENT;");
        Execute(writer, "INSERT INTO t VALUES (91);");
        Execute(writer, "COMMIT;");

        ReadInteger(reader, "SELECT COUNT(*) FROM t WHERE v = 91;").Should().Be(
            0,
            "the reader's pinned snapshot predates the writer's commit");
        Execute(reader, "ROLLBACK;");

        ReadInteger(writer, "SELECT COUNT(*) FROM t WHERE v = 91;").Should().Be(1);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static long ReadInteger(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsInteger();

    private static string ReadText(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsText();
}
