using Ahtola.Core;
using Ahtola.Core.Parsing;
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
    public void CommittingAWriteNoLongerForceLoadsAnUntouchedIndexlessRowidAliasSibling()
    {
        // This test previously documented an open boundary of this slice
        // ("CommittingAWriteEagerlyValidatesEveryOtherTableIncludingUntouchedSiblings"):
        // PersistCore's ValidateTableRepresentable pass ran the rowid-alias INTEGER PRIMARY
        // KEY uniqueness loop unconditionally over every table in the catalog on every
        // committed write, forcing full hydration of every untouched sibling with that common
        // table shape. That boundary is now closed: the loop is skipped whenever
        // IsTableRowStorageUnchangedFromPrevious proves the sibling's row storage has not
        // changed since the last successful commit — sound because a rowid-alias column's
        // stored value always equals the row's own committed rowid, and
        // EmbeddedFileStore.Load()'s structural pass already guarantees every table b-tree's
        // rowids are strictly increasing/distinct, so an on-disk, never-mutated table's
        // rowid-alias values cannot ever be duplicated; only an actual write (which always
        // bumps RowStore.Revision) could introduce a duplicate, and that case is still fully
        // checked (see CommittingADuplicateRowidAliasValueIsStillCaught below).
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
        using var connection2 = reopened.Connect();

        reopened.LiveCatalog.Tables["sibling"].HasPendingRowLoad.Should().BeTrue();
        Execute(connection2, "INSERT INTO touched VALUES (2, 'b');");
        // Every commit republishes EmbeddedDatabase's whole table dictionary from a fresh
        // SchemaCatalog.Clone() (see PublishCatalog), so LiveCatalog must be re-fetched after
        // each write to observe the current generation: even an untouched table's entry
        // becomes a distinct EmbeddedTable instance on every commit (sharing lazy state with
        // its predecessor via TryCopyPendingRowLoadTo, never the same reference).
        reopened.LiveCatalog.Tables["sibling"].HasPendingRowLoad.Should().BeTrue(
            "a committed write to an unrelated table must no longer force-load an untouched " +
            "indexless rowid-alias sibling");

        ReadRows(connection2, "SELECT id, value FROM touched ORDER BY id;")
            .Select(row => (row[0].AsInteger(), row[1].AsText()))
            .Should()
            .Equal((1L, "a"), (2L, "b"));
        ReadText(connection2, "SELECT value FROM sibling WHERE id = 1;").Should().Be("z");
        reopened.LiveCatalog.Tables["sibling"].HasPendingRowLoad.Should().BeFalse(
            "actually reading 'sibling' still hydrates it, same as always");
    }

    [Test]
    public void CommittingAnActualDuplicateRowidAliasValueIsStillCaught()
    {
        // Correctness companion to the boundary-closing test above: skipping the rowid-alias
        // uniqueness loop for an UNCHANGED table must never let a genuine, freshly-introduced
        // duplicate slip through undetected on the table that actually mutated.
        var fileSystem = new InMemoryFileSystem();
        const string path = "duplicate-rowid-alias.db";

        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'b');");

        // sqlite_autoindex/rowid-alias enforcement normally rejects this at the VDBE layer
        // before it ever reaches PersistCore's validation; this proves the *storage-layer*
        // uniqueness check (the one this change gates) independently, the same way the
        // pre-existing unconditional loop was exercised, by going through EmbeddedFileStore
        // directly with a row set the VDBE layer never got a chance to validate.
        var fileStore = database.FileStore!;
        var table = database.LiveCatalog.Tables["t"];
        var duplicated = table.Clone();
        duplicated.Rows[1] = [SqlValue.Integer(1), SqlValue.Text("duplicate")];

        var tables = new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["t"] = duplicated,
        };
        var views = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        var triggers = new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase);
        var virtualTables = new Dictionary<string, EmbeddedDatabase.VirtualTableDefinition>(
            StringComparer.OrdinalIgnoreCase);

        Action persistDuplicate = () => fileStore.Persist(tables, views, triggers, virtualTables);
        persistDuplicate.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*INTEGER PRIMARY KEY*contains duplicate values*");
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

    [Test]
    public void BeginningAClassicTransactionHydratesAStillPendingReopenedTableSoTheReaderStaysIsolated()
    {
        // Regression coverage for the fix in EmbeddedDatabase.CreateTransactionSnapshotWithPin:
        // a classic (non-MVCC) transaction pins a real pager read snapshot and clones the
        // catalog for its own exclusive use at BEGIN time, so its isolation promise (a
        // consistent view for the transaction's whole lifetime) can only hold if every table
        // its own clone might later touch is hydrated from that exact same, already-fixed
        // generation — not lazily, mid-transaction, from whatever the store's live pager holds
        // by then (which could already include a peer's later commit). Only the transaction's
        // own working-copy clone is force-hydrated; the shared, published catalog stays lazy
        // for a table nothing has ever touched (see the assertion on 'catalog' below).
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-classic-tx-isolation.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(v INTEGER);");
            Execute(connection, "INSERT INTO t VALUES (1);");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var catalog = reopened.LiveCatalog;
        catalog.Tables["t"].HasPendingRowLoad.Should().BeTrue(
            "a reopened, indexless table must still be lazy immediately after physical open");

        using var reader = reopened.Connect();
        using var writer = reopened.Connect();

        Execute(reader, "BEGIN;");
        Execute(writer, "INSERT INTO t VALUES (91);");

        ReadInteger(reader, "SELECT COUNT(*) FROM t WHERE v = 91;").Should().Be(
            0,
            "the classic reader's pinned snapshot predates the writer's autocommit insert");
        Execute(reader, "COMMIT;");

        ReadInteger(reader, "SELECT COUNT(*) FROM t WHERE v = 91;").Should().Be(
            1,
            "once the reader's own transaction ends, a fresh read observes the committed insert");
    }

    [Test]
    public void DisposingAConnectionWithStillPendingTablesDoesNotThrowOrLeak()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "lazy-dispose.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE untouched_one(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE untouched_two(id INTEGER PRIMARY KEY, value TEXT);
                """);
            Execute(connection, "INSERT INTO untouched_one VALUES (1, 'a');");
            Execute(connection, "INSERT INTO untouched_two VALUES (1, 'b');");
        }

        // Open, touch nothing, and dispose repeatedly: every table stays lazy for the whole
        // connection lifetime, and disposal must neither throw nor leave the file store unable
        // to be reopened cleanly afterward (no dangling pager state from an abandoned load).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var database = EmbeddedDatabase.OpenFile(path, fileSystem);
            var catalog = database.LiveCatalog;
            catalog.Tables["untouched_one"].HasPendingRowLoad.Should().BeTrue();
            catalog.Tables["untouched_two"].HasPendingRowLoad.Should().BeTrue();
            var connection = database.Connect();
            connection.Dispose();
            database.Dispose();
        }

        using var verifier = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var verifierConnection = verifier.Connect();
        ReadText(verifierConnection, "SELECT value FROM untouched_one WHERE id = 1;").Should().Be("a");
        ReadText(verifierConnection, "SELECT value FROM untouched_two WHERE id = 1;").Should().Be("b");
    }

    [Test]
    public void ALoaderThatFailsPartwayNeverStrandsATruncatedRowSet()
    {
        // Regression coverage for a critical bug found in review: EnsureRowsLoaded used to flip
        // _rowsLoaded to true (and discard the pending loader/lease) BEFORE invoking the loader,
        // so that a loader which appended some rows and then threw left the table permanently
        // and silently marked "successfully loaded" with only its partial rows. Every later
        // reader -- including a subsequent VACUUM/persist pass -- would then treat that
        // truncated row set as complete and correct, an actual data-loss bug.
        var fileSystem = new InMemoryFileSystem();
        const string path = "failed-lazy-load.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var table = reopened.LiveCatalog.Tables["t"];
        table.HasPendingRowLoad.Should().BeTrue();

        // Replace the store's real committed-page loader with a synthetic one that reproduces
        // the reported repro exactly: it appends one row, then throws, simulating a page decode
        // failure partway through a real load.
        table.AttachPendingRowLoader(loadingTable =>
        {
            loadingTable.Rows.Add([SqlValue.Integer(1), SqlValue.Text("a")]);
            loadingTable.RowIds.Add(1);
            throw new InvalidDataException("simulated mid-load page corruption");
        });

        Action firstAccess = () => _ = table.Rows.Count;
        firstAccess.Should().Throw<InvalidDataException>().WithMessage("simulated mid-load page corruption");

        table.HasFailedRowLoad.Should().BeTrue();
        table.HasPendingRowLoad.Should().BeFalse(
            "a permanently failed load is neither successfully loaded nor still pending a retry");

        // The critical regression: a second access must keep failing closed, never silently
        // return the one partial row the failed attempt appended before throwing.
        Action secondAccess = () => _ = table.Rows.Count;
        secondAccess.Should().Throw<InvalidOperationException>()
            .WithMessage("*rows can no longer be trusted*");
        Action thirdAccessViaRowIds = () => _ = table.RowIds.Count;
        thirdAccessViaRowIds.Should().Throw<InvalidOperationException>()
            .WithMessage("*rows can no longer be trusted*");

        // Cloning is the per-statement working-copy path every statement takes (see
        // EmbeddedTable.Clone/AdoptContentFrom); it must propagate the same failure rather than
        // silently succeeding with an empty or partially populated clone.
        Action cloneAttempt = () => table.Clone();
        cloneAttempt.Should().Throw<InvalidOperationException>()
            .WithMessage("*rows can no longer be trusted*");
    }

    [Test]
    public void VacuumFailsClosedInsteadOfPersistingATableWhoseLazyLoadFailed()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "failed-lazy-load-vacuum.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE t(id INTEGER PRIMARY KEY, value TEXT);
                CREATE TABLE sibling(id INTEGER PRIMARY KEY, value TEXT);
                """);
            Execute(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c');");
            Execute(connection, "INSERT INTO sibling VALUES (1, 'z');");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        var table = reopened.LiveCatalog.Tables["t"];
        table.AttachPendingRowLoader(loadingTable =>
        {
            loadingTable.Rows.Add([SqlValue.Integer(1), SqlValue.Text("a")]);
            loadingTable.RowIds.Add(1);
            throw new InvalidDataException("simulated mid-load page corruption");
        });

        using var connection2 = reopened.Connect();

        // VACUUM (EmbeddedDatabase.MigratePageSize) forces every still-lazy table in the live
        // catalog to hydrate before rewriting the file, specifically so a still-pending table
        // never gets compared against reassigned root pages -- see 812a106. That same forced
        // hydration must now surface this table's permanent load failure instead of silently
        // vacuuming the database down to only the rows that happened to load before the fault.
        Action vacuum = () => Execute(connection2, "VACUUM;");
        vacuum.Should().Throw<Exception>(
            "VACUUM must fail closed rather than persist a truncated table to disk");

        table.HasFailedRowLoad.Should().BeTrue();
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
