using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Covers the attached-database parity workstream's cross-schema read/write routing
/// (<c>RouteMultiSchemaReadQuery</c>, <c>TryRouteSingleWriteMultiSchemaStatement</c>) and the
/// relaxed multi-database transaction guard (<c>EnsureTransactionMayMutate</c>,
/// <c>EmbeddedConnection.CommitTransaction</c>): cross-database reads/writes across physical and
/// in-memory attachments, main/temp/attached schema shadowing, rollback after a mid-transaction
/// failure, savepoints spanning more than one attached database, multiple in-memory databases
/// committing together, durability across a physical-file disk reopen, reversible ':memory:'
/// catalog publication when a later database in the same commit fails, and CDC routed to an
/// attached database's foreign turso_cdc owner.
/// </summary>
public sealed class ManagedAttachCrossDatabaseTests
{
    [Test]
    public void CrossDatabaseJoinAcrossTwoPhysicalAttachmentsSurvivesDiskReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("xdb-join-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'xdb-join-aux1.db' AS aux1;");
            Execute(connection, "ATTACH DATABASE 'xdb-join-aux2.db' AS aux2;");
            Execute(connection, "CREATE TABLE main.orders(id INTEGER PRIMARY KEY, customer_id INTEGER);");
            Execute(connection, "CREATE TABLE aux1.customers(id INTEGER PRIMARY KEY, name TEXT);");
            Execute(connection, "CREATE TABLE aux2.regions(customer_id INTEGER, region TEXT);");
            Execute(connection, "INSERT INTO main.orders VALUES (1, 10);");
            Execute(connection, "INSERT INTO aux1.customers VALUES (10, 'Ada');");
            Execute(connection, "INSERT INTO aux2.regions VALUES (10, 'north');");

            ReadRows(
                    connection,
                    "SELECT c.name, r.region FROM main.orders o "
                    + "JOIN aux1.customers c ON o.customer_id = c.id "
                    + "JOIN aux2.regions r ON o.customer_id = r.customer_id;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("Ada"), SqlValue.Text("north"));
        }

        // Every table lives in a different physical file: reopening main and re-attaching both
        // auxiliaries proves the cross-database write path durably persisted each one to its own
        // file, not just to a shared in-memory catalog.
        using var reopenedMain = EmbeddedDatabase.OpenFile("xdb-join-main.db", fileSystem);
        using var reopenedConnection = reopenedMain.Connect();
        Execute(reopenedConnection, "ATTACH DATABASE 'xdb-join-aux1.db' AS aux1;");
        Execute(reopenedConnection, "ATTACH DATABASE 'xdb-join-aux2.db' AS aux2;");
        ReadRows(
                reopenedConnection,
                "SELECT c.name, r.region FROM main.orders o "
                + "JOIN aux1.customers c ON o.customer_id = c.id "
                + "JOIN aux2.regions r ON o.customer_id = r.customer_id;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("Ada"), SqlValue.Text("north"));
    }

    [Test]
    public void CrossDatabaseInsertSelectPersistsAcrossDiskReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("xdb-persist-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'xdb-persist-aux.db' AS aux;");
            Execute(connection, "CREATE TABLE main.source(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "CREATE TABLE aux.mirror(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO main.source VALUES (1, 'a'), (2, 'b');");
            Execute(connection, "INSERT INTO aux.mirror SELECT * FROM main.source;");
        }

        using var reopenedAux = EmbeddedDatabase.OpenFile("xdb-persist-aux.db", fileSystem);
        using var reopenedConnection = reopenedAux.Connect();
        AssertRows(
            ReadRows(reopenedConnection, "SELECT id, value FROM mirror ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("a")],
            [SqlValue.Integer(2), SqlValue.Text("b")]);
    }

    [Test]
    public void MultipleInMemoryAttachmentsCommitTogetherInOneTransaction()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux1;");
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux2;");
        Execute(connection, "CREATE TABLE aux1.t1(value TEXT);");
        Execute(connection, "CREATE TABLE aux2.t2(value TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO aux1.t1 VALUES ('one');");
        Execute(connection, "INSERT INTO aux2.t2 VALUES ('two');");
        Execute(connection, "COMMIT;");

        ReadRows(connection, "SELECT value FROM aux1.t1;").Should().ContainSingle().Which.Should().Equal(SqlValue.Text("one"));
        ReadRows(connection, "SELECT value FROM aux2.t2;").Should().ContainSingle().Which.Should().Equal(SqlValue.Text("two"));
    }

    [Test]
    public void PhysicalMainPlusInMemoryAttachmentsStillRejectMixedWritesInOneTransaction()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("xdb-mixed-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux1;");
        Execute(connection, "CREATE TABLE main.m(value TEXT);");
        Execute(connection, "CREATE TABLE aux1.a1(value TEXT);");

        // A physical database's write must still be the ONLY database this transaction touches -
        // not just "at most one OTHER physical database". Reversible/staged ':memory:'
        // publication proven safe alongside a *simultaneous* physical commit (preflighting every
        // fallible piece of the memory publish while holding locks through the physical commit)
        // is not implemented, so mixing a physical write with a ':memory:' one in the same
        // transaction stays rejected until that design exists (see EnsureTransactionMayMutate's
        // doc comment) - unlike multiple ':memory:' databases together, which is provably safe
        // and supported (see MultipleInMemoryAttachmentsCommitTogetherInOneTransaction).
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO main.m VALUES ('main-write');");
        var mixedWrite = () => Execute(connection, "INSERT INTO aux1.a1 VALUES ('aux1-write');");
        mixedWrite.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*cannot modify more than one database*atomically*");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, "SELECT count(*) FROM main.m;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
        ReadRows(connection, "SELECT count(*) FROM aux1.a1;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void MultiDatabaseTransactionRollsBackAllDatabasesAfterMidTransactionFailure()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE main.m(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "CREATE TABLE aux.a(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO main.m VALUES (1, 'existing');");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO aux.a VALUES (1, 'aux-pending');");
        var conflicting = () => Execute(connection, "INSERT INTO main.m VALUES (1, 'duplicate');");
        conflicting.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE constraint failed*");
        Execute(connection, "ROLLBACK;");

        // The failed statement only touched main; the earlier, successful write to aux within
        // the same still-open transaction must also be undone by the rollback.
        ReadRows(connection, "SELECT count(*) FROM aux.a;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
        ReadRows(connection, "SELECT value FROM main.m WHERE id = 1;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("existing"));
    }

    [Test]
    public void SavepointRollbackUndoesOnlyWritesAfterTheSavepointAcrossDatabases()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE main.m(value TEXT);");
        Execute(connection, "CREATE TABLE aux.a(value TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO main.m VALUES ('before-savepoint');");
        Execute(connection, "SAVEPOINT sp;");
        Execute(connection, "INSERT INTO aux.a VALUES ('after-savepoint');");
        Execute(connection, "ROLLBACK TO sp;");
        Execute(connection, "COMMIT;");

        ReadRows(connection, "SELECT value FROM main.m;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("before-savepoint"));
        ReadRows(connection, "SELECT count(*) FROM aux.a;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void UnqualifiedNameResolvesTempOverMainOverAttachedWhenAllThreeShareIt()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE main.shared(value TEXT);");
        Execute(connection, "CREATE TABLE aux.shared(value TEXT);");
        Execute(connection, "CREATE TEMP TABLE shared(value TEXT);");
        Execute(connection, "INSERT INTO main.shared VALUES ('main');");
        Execute(connection, "INSERT INTO aux.shared VALUES ('aux');");
        Execute(connection, "INSERT INTO temp.shared VALUES ('temp');");

        // SQLite's shadowing rule: an unqualified reference resolves to temp first, then main,
        // then the least-recently-attached database - never the attached one when temp/main both
        // already have a same-named table.
        ReadRows(connection, "SELECT value FROM shared;").Should().ContainSingle().Which.Should().Equal(SqlValue.Text("temp"));
        ReadRows(connection, "SELECT value FROM main.shared;").Should().ContainSingle().Which.Should().Equal(SqlValue.Text("main"));
        ReadRows(connection, "SELECT value FROM aux.shared;").Should().ContainSingle().Which.Should().Equal(SqlValue.Text("aux"));
    }

    [Test]
    public void CrossDatabaseReadJoiningMainTempAndAttachedAllInOneQuery()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE main.m(id INTEGER, value TEXT);");
        Execute(connection, "CREATE TEMP TABLE t(id INTEGER, value TEXT);");
        Execute(connection, "CREATE TABLE aux.a(id INTEGER, value TEXT);");
        Execute(connection, "INSERT INTO main.m VALUES (1, 'main');");
        Execute(connection, "INSERT INTO temp.t VALUES (1, 'temp');");
        Execute(connection, "INSERT INTO aux.a VALUES (1, 'aux');");

        ReadRows(
                connection,
                "SELECT m.value, t.value, a.value FROM main.m "
                + "JOIN temp.t ON m.id = t.id JOIN aux.a ON m.id = a.id;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("main"), SqlValue.Text("temp"), SqlValue.Text("aux"));
    }

    [Test]
    public void TwoPhysicalAttachmentsStillRejectConcurrentWritesInOneTransaction()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("xdb-guard-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE 'xdb-guard-aux1.db' AS aux1;");
        Execute(connection, "ATTACH DATABASE 'xdb-guard-aux2.db' AS aux2;");
        Execute(connection, "CREATE TABLE aux1.t1(value TEXT);");
        Execute(connection, "CREATE TABLE aux2.t2(value TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO aux1.t1 VALUES ('one');");
        var secondPhysicalWrite = () => Execute(connection, "INSERT INTO aux2.t2 VALUES ('two');");
        secondPhysicalWrite.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*cannot modify more than one database*");
        Execute(connection, "ROLLBACK;");
    }

    [Test]
    public void MultipleMemoryDatabaseCommitFaultRevertsTheEarlierPublishedDatabaseEndToEnd()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux1;");
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux2;");
        Execute(connection, "CREATE TABLE aux1.a1(value TEXT);");
        Execute(connection, "CREATE TABLE aux2.a2(value TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO aux1.a1 VALUES ('aux1-write');");
        Execute(connection, "INSERT INTO aux2.a2 VALUES ('aux2-write');");

        // BeginTransaction snapshots main, temp, then attached databases sorted by path
        // identity, so aux1 is committed (published) before aux2 in CommitTransaction()'s
        // persistentChanges loop. Force the SECOND database's commit to fail via the test-only
        // hook - exercising the real CommitTransaction() code path end-to-end, not a hand-rolled
        // simulation of it - and verify the FIRST database's already-published catalog is
        // correctly reverted to its pre-commit state rather than silently staying visible even
        // though the surrounding COMMIT never actually completed. This is the stronger,
        // end-to-end counterpart to RevertPublishedMemoryCatalogRestoresThePreRevertCatalogState
        // (which only exercises the isolated revert primitive directly).
        var invocationCount = 0;
        EmbeddedConnection.BeforeCommittingPersistentChangeForTesting = _ =>
        {
            if (++invocationCount == 2)
                throw new IOException("Injected second-database commit failure.");
        };
        try
        {
            var failingCommit = () => Execute(connection, "COMMIT;");
            failingCommit.Should().Throw<IOException>();
        }
        finally
        {
            EmbeddedConnection.BeforeCommittingPersistentChangeForTesting = null;
        }

        Execute(connection, "ROLLBACK;");
        ReadRows(connection, "SELECT count(*) FROM aux1.a1;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
        ReadRows(connection, "SELECT count(*) FROM aux2.a2;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));

        // The connection must recover cleanly for further use, not be left in some half-open state.
        Execute(connection, "INSERT INTO aux1.a1 VALUES ('after-recovery');");
        ReadRows(connection, "SELECT count(*) FROM aux1.a1;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
    }

    [Test]
    public void RevertPublishedMemoryCatalogRestoresThePreRevertCatalogState()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(value TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('before');");

        var beforeCatalog = database.LiveCatalog;

        // Mirror what a real multi-database commit publishes for a ':memory:' database: a
        // distinct clone (a transaction's own staged catalog), never an in-place mutation of the
        // live tables captured in beforeCatalog - PublishTriggerBodyCatalog is the same "publish
        // some other catalog object outside the normal per-statement Execute flow" primitive
        // EmbeddedConnection.CommitTransaction itself calls (by way of EmbeddedDatabase's own
        // CommitTransaction) for a ':memory:' database.
        var afterCatalog = beforeCatalog.Clone();
        var afterTable = afterCatalog.Tables["t"];
        afterTable.Rows.Add([SqlValue.Text("after")]);
        afterTable.RowIds.Add(2);
        database.PublishTriggerBodyCatalog(afterCatalog, forceFullRewrite: false);
        ReadRows(connection, "SELECT count(*) FROM t;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));

        // Directly exercises EmbeddedDatabase.RevertPublishedMemoryCatalog, the mechanism
        // EmbeddedConnection.CommitTransaction uses to undo an earlier ':memory:' database's
        // publish when a later database in the same multi-database commit fails.
        database.RevertPublishedMemoryCatalog(beforeCatalog);

        ReadRows(connection, "SELECT value FROM t;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("before"));
    }

    [Test]
    public void RevertPublishedMemoryCatalogThrowsForAPhysicalDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("xdb-revert-guard-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "CREATE TABLE t(value TEXT);");
        var previous = main.LiveCatalog;

        var revert = () => main.RevertPublishedMemoryCatalog(previous);
        revert.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void CdcCaptureOnAttachedDatabasePersistsToPhysicalOwnerAfterAutocommitWrite()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("xdb-cdc-autocommit-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE turso_cdc(change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, "
                + "change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB);");
            Execute(connection, "PRAGMA capture_data_changes_conn('full');");
            Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
            Execute(connection, "CREATE TABLE aux.t1(x INTEGER, y TEXT);");
            // Autocommit: the write target (aux) is ':memory:', but the CDC owner (main, holding
            // turso_cdc) is physical. Nothing in the routed statement's own commit persists main,
            // since main isn't the routed database - only PublishForeignCdcCommitIfNeeded does that.
            Execute(connection, "INSERT INTO aux.t1 VALUES (1, 'hello');");
            ReadRows(connection, "SELECT table_name, change_type FROM turso_cdc WHERE table_name != 'sqlite_schema';")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("t1"), SqlValue.Integer(1));
        }

        // aux (':memory:') cannot survive a reopen, but main is physical: reopening it alone
        // must still show the CDC row, proving the autocommit path durably published the
        // foreign owner rather than leaving the mutation visible only in-process.
        using var reopenedMain = EmbeddedDatabase.OpenFile("xdb-cdc-autocommit-main.db", fileSystem);
        using var reopenedConnection = reopenedMain.Connect();
        ReadRows(reopenedConnection, "SELECT table_name, change_type FROM turso_cdc WHERE table_name != 'sqlite_schema';")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("t1"), SqlValue.Integer(1));
    }

    [Test]
    public void CdcCaptureOnAttachedDatabasePersistsAfterExplicitTransactionCommit()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(
            connection,
            "CREATE TABLE turso_cdc(change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, "
            + "change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB);");
        Execute(connection, "PRAGMA capture_data_changes_conn('full');");
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE aux.t1(x INTEGER, y TEXT);");

        // Regression for the CDC owner never being marked HasChanges: an explicit COMMIT used to
        // silently drop the CDC row appended to main's turso_cdc while writing to the attached aux.
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO aux.t1 VALUES (1, 'hello');");
        Execute(connection, "COMMIT;");

        ReadRows(connection, "SELECT table_name, change_type FROM turso_cdc WHERE table_name != 'sqlite_schema';")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("t1"), SqlValue.Integer(1));
    }

    [Test]
    public void CdcCaptureOnAttachedDatabaseIsDiscardedByExplicitRollback()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();
        Execute(
            connection,
            "CREATE TABLE turso_cdc(change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, "
            + "change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB);");
        Execute(connection, "PRAGMA capture_data_changes_conn('full');");
        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE aux.t1(x INTEGER, y TEXT);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO aux.t1 VALUES (1, 'hello');");
        Execute(connection, "ROLLBACK;");

        // Now that the CDC owner is a real tracked transaction participant, an intervening
        // ROLLBACK must discard its appended row exactly like any other write in that transaction.
        ReadRows(connection, "SELECT count(*) FROM aux.t1;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
        ReadRows(connection, "SELECT count(*) FROM turso_cdc WHERE table_name != 'sqlite_schema';")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void CdcCaptureOnAttachedDatabasePersistsThePartialPrefixAfterOnConflictFail()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("xdb-cdc-conflict-fail-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE turso_cdc(change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, "
                + "change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB);");
            Execute(connection, "PRAGMA capture_data_changes_conn('full');");
            Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
            Execute(connection, "CREATE TABLE aux.t1(x INTEGER UNIQUE);");

            // ON CONFLICT FAIL durably keeps the rows already written before the conflict
            // (x=1) even though the statement as a whole throws - including whatever the
            // foreign CDC owner's clone captured for that prefix. Before the outer
            // EmbeddedConflictFailException catch also called PublishForeignCdcCommitIfNeeded,
            // this row was silently dropped: the publish only ran on the success path, which a
            // thrown EmbeddedConflictFailException never reaches.
            var conflicting = () => Execute(connection, "INSERT OR FAIL INTO aux.t1 VALUES (1), (1);");
            conflicting.Should().Throw<EmbeddedSqlException>();

            ReadRows(connection, "SELECT count(*) FROM aux.t1;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
            ReadRows(connection, "SELECT table_name, change_type FROM turso_cdc WHERE table_name != 'sqlite_schema';")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("t1"), SqlValue.Integer(1));
        }

        // main is physical: reopening it alone must still show the CDC row for the preserved
        // prefix, proving the ON CONFLICT FAIL path durably published the foreign owner.
        using var reopenedMain = EmbeddedDatabase.OpenFile("xdb-cdc-conflict-fail-main.db", fileSystem);
        using var reopenedConnection = reopenedMain.Connect();
        ReadRows(reopenedConnection, "SELECT table_name, change_type FROM turso_cdc WHERE table_name != 'sqlite_schema';")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("t1"), SqlValue.Integer(1));
    }

    [Test]
    public void CdcRoutingToASecondPhysicalOwnerIsRejectedImmediatelyNotAtCommit()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("xdb-cdc-gate-main.db", fileSystem);
        using var connection = main.Connect();
        // PRAGMA capture_data_changes_conn always auto-provisions its turso_cdc table on main
        // (CurrentMainCatalog), regardless of any same-named table elsewhere - so the only
        // reachable "foreign CDC owner" is main itself when the write target is a different
        // schema, never an arbitrary attached database the user pre-created the table in.
        Execute(connection, "PRAGMA capture_data_changes_conn('full');");
        Execute(connection, "ATTACH DATABASE 'xdb-cdc-gate-aux.db' AS auxphys;");
        Execute(connection, "CREATE TABLE auxphys.p(value TEXT);");

        Execute(connection, "BEGIN;");
        // auxphys (physical #1, the routed write target) and main (physical #2, the CDC owner)
        // are two DIFFERENT physical databases this single statement would need to make change
        // together, which is just as much an immediate two-physical-database violation as if an
        // earlier statement had already completed a write to some other physical database - and
        // must be rejected right here (fail fast), not silently allowed until the unrelated,
        // much later InvalidOperationException CommitTransaction would otherwise throw once both
        // physical databases' HasChanges finally surface together at COMMIT.
        var cdcThroughSecondPhysical = () => Execute(connection, "INSERT INTO auxphys.p VALUES ('should-not-run');");
        cdcThroughSecondPhysical.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*cannot modify more than one database*");

        // The rejected statement must not have partially mutated anything - not auxphys (its own
        // write target) and not the CDC owner's backing table.
        ReadRows(connection, "SELECT count(*) FROM auxphys.p;").Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
        Execute(connection, "ROLLBACK;");
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
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

    private static void AssertRows(IReadOnlyList<SqlValue[]> actual, params SqlValue[][] expected)
    {
        actual.Count.Should().Be(expected.Length);
        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
            actual[rowIndex].Should().Equal(expected[rowIndex]);
    }
}
