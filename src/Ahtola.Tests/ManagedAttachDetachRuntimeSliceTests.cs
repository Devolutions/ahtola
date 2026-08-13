using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedAttachDetachRuntimeSliceTests
{
    private const string Aes256Key = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string OtherAes256Key = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    [Test]
    public void DirectManagedAttachRoutesSchemaQualifiedDdlDmlAndQueriesAcrossDetach()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var main = EmbeddedDatabase.OpenFile("attach-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'attach-secondary.db' AS aux;");
            Execute(connection, "CREATE TABLE main.items(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO main.items VALUES (1, 'main');");
            var databases = ReadRows(connection, "PRAGMA database_list;");
            databases.Should().HaveCount(2);
            databases[0].Should().Equal(
                SqlValue.Integer(0),
                SqlValue.Text("main"),
                SqlValue.Text("attach-main.db"));
            databases[1].Should().Equal(
                SqlValue.Integer(2),
                SqlValue.Text("aux"),
                SqlValue.Text("attach-secondary.db"));

            Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO aux.items VALUES (1, 'persisted');");
            Execute(connection, "CREATE TABLE aux.only_aux(value TEXT);");
            Execute(connection, "INSERT INTO aux.only_aux VALUES ('fallback');");
            Execute(connection, "INSERT INTO aux.items SELECT 2, value || '-copy' FROM aux.items WHERE id = 1;");
            Execute(
                connection,
                "UPDATE aux.items SET value = (SELECT value || '-updated' FROM aux.items WHERE id = 1) WHERE id = 2;");
            ReadRows(connection, "SELECT value FROM aux.items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));
            ReadRows(connection, "SELECT value FROM items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("main"));
            ReadRows(connection, "SELECT value FROM only_aux;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("fallback"));
            ReadRows(
                    connection,
                    "WITH selected AS (SELECT id, value FROM aux.items WHERE id > 0) "
                    + "SELECT value FROM selected WHERE id IN (SELECT id FROM aux.items WHERE id = 2);")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted-updated"));

            fileSystem.FileExists("attach-secondary.db").Should().BeTrue();
            fileSystem.FileExists("attach-secondary.db-wal").Should().BeTrue();

            Execute(connection, "DETACH DATABASE aux;");
            ReadRows(connection, "PRAGMA database_list;").Should().ContainSingle()
                .Which.Should().Equal(
                    SqlValue.Integer(0),
                    SqlValue.Text("main"),
                    SqlValue.Text("attach-main.db"));
            var detached = () => ReadRows(connection, "SELECT value FROM aux.items;");
            detached.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: aux");

            Execute(connection, "ATTACH DATABASE 'attach-secondary.db' AS aux;");
            ReadRows(connection, "PRAGMA database_list;")[1][0].Should().Be(SqlValue.Integer(2));
            ReadRows(connection, "SELECT value FROM aux.items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));
            Execute(connection, "DETACH aux;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("attach-secondary.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            ReadRows(connection, "SELECT value FROM items WHERE id = 1;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));
        }

        fileSystem.DeleteFile("attach-secondary.db");
        fileSystem.DeleteFile("attach-secondary.db-wal");
        fileSystem.FileExists("attach-secondary.db").Should().BeFalse();
        fileSystem.FileExists("attach-secondary.db-wal").Should().BeFalse();
    }

    [Test]
    public void InMemoryPrimaryAttachesIndependentConnectionOwnedMemoryDatabases()
    {
        using var main = new EmbeddedDatabase();
        using var connection = main.Connect();

        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "ATTACH DATABASE ':memory:' AS other;");
        Execute(connection, "CREATE TABLE main.items(value TEXT);");
        Execute(connection, "CREATE TABLE aux.items(value TEXT);");
        Execute(connection, "CREATE TABLE other.items(value TEXT);");
        Execute(connection, "INSERT INTO main.items VALUES ('main');");
        Execute(connection, "INSERT INTO aux.items VALUES ('aux');");
        Execute(connection, "INSERT INTO other.items VALUES ('other');");

        var databases = ReadRows(connection, "PRAGMA database_list;");
        databases.Should().HaveCount(3);
        databases[0].Should().Equal(SqlValue.Integer(0), SqlValue.Text("main"), SqlValue.Text(string.Empty));
        databases[1].Should().Equal(SqlValue.Integer(2), SqlValue.Text("aux"), SqlValue.Text(string.Empty));
        databases[2].Should().Equal(SqlValue.Integer(3), SqlValue.Text("other"), SqlValue.Text(string.Empty));
        ReadRows(connection, "SELECT value FROM main.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("main"));
        ReadRows(connection, "SELECT value FROM aux.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("aux"));
        ReadRows(connection, "SELECT value FROM other.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("other"));

        Execute(connection, "BEGIN;");
        var locked = () => Execute(connection, "DETACH DATABASE other;");
        locked.Should().Throw<EmbeddedSqlException>().WithMessage("database other is locked");
        Execute(connection, "ROLLBACK;");
        Execute(connection, "DETACH DATABASE aux;");
        Execute(connection, "DETACH DATABASE other;");
        var detached = () => ReadRows(connection, "SELECT value FROM aux.items;");
        detached.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: aux");
    }

    [Test]
    public void FileBackedPrimaryCanAttachConnectionOwnedMemoryDatabase()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-memory-main.db", fileSystem);
        using var connection = main.Connect();

        Execute(connection, "ATTACH DATABASE ':memory:' AS aux;");
        Execute(connection, "CREATE TABLE aux.items(value TEXT);");
        Execute(connection, "INSERT INTO aux.items VALUES ('memory');");

        ReadRows(connection, "SELECT value FROM aux.items;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("memory"));
        fileSystem.FileExists(":memory:").Should().BeFalse();
    }

    [Test]
    public void AttachedWithoutRowidCatalogCommitsRollsBackAndReopens()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("attach-without-rowid-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'attach-without-rowid-aux.db' AS aux;");
            Execute(connection, """
                CREATE TABLE aux.entry(
                    tenant TEXT,
                    sequence INTEGER,
                    value TEXT,
                    computed INTEGER GENERATED ALWAYS AS (sequence + 1) VIRTUAL,
                    PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC),
                    UNIQUE(value)
                ) WITHOUT ROWID;
                """);
            Execute(connection, "CREATE INDEX aux.entry_computed ON entry(computed DESC);");
            Execute(connection, "BEGIN;");
            Execute(connection, "INSERT INTO aux.entry(tenant, sequence, value) VALUES ('alpha', 1, 'one');");
            Execute(connection, "INSERT INTO aux.entry(tenant, sequence, value) VALUES ('Alpha', 2, 'two');");
            Execute(connection, "COMMIT;");

            Execute(connection, "BEGIN;");
            Execute(connection, "UPDATE aux.entry SET sequence = 9 WHERE value = 'one';");
            Execute(connection, "INSERT INTO aux.entry(tenant, sequence, value) VALUES ('beta', 3, 'rolled-back');");
            Execute(connection, "ROLLBACK;");
            var attachedRows = ReadRows(
                connection,
                "SELECT tenant, sequence, value, computed FROM aux.entry ORDER BY sequence DESC;");
            attachedRows.Should().HaveCount(2);
            attachedRows[0].Should().Equal(
                SqlValue.Text("Alpha"),
                SqlValue.Integer(2),
                SqlValue.Text("two"),
                SqlValue.Integer(3));
            attachedRows[1].Should().Equal(
                SqlValue.Text("alpha"),
                SqlValue.Integer(1),
                SqlValue.Text("one"),
                SqlValue.Integer(2));
            Execute(connection, "DETACH aux;");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("attach-without-rowid-aux.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            var reopenedRows = ReadRows(
                connection,
                "SELECT tenant, sequence, value, computed FROM entry ORDER BY sequence DESC;");
            reopenedRows.Should().HaveCount(2);
            reopenedRows[0].Should().Equal(
                SqlValue.Text("Alpha"),
                SqlValue.Integer(2),
                SqlValue.Text("two"),
                SqlValue.Integer(3));
            reopenedRows[1].Should().Equal(
                SqlValue.Text("alpha"),
                SqlValue.Integer(1),
                SqlValue.Text("one"),
                SqlValue.Integer(2));
            ReadRows(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'entry_computed';")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
        }

        fileSystem.DeleteFile("attach-without-rowid-main.db");
        fileSystem.DeleteFile("attach-without-rowid-main.db-wal");
        fileSystem.DeleteFile("attach-without-rowid-aux.db");
        fileSystem.DeleteFile("attach-without-rowid-aux.db-wal");
    }

    [Test]
    public void DirectManagedAttachRoutesLimitedDmlAndPreservesOneDatabaseWrites()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-limited-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "CREATE TABLE main.items(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO main.items VALUES (1, 'main');");
        Execute(connection, "ATTACH DATABASE 'attach-limited-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY, rank INTEGER, value TEXT);");
        Execute(connection, "INSERT INTO aux.items VALUES (1, NULL, 'one'), (2, 1, 'two'), (3, 2, 'three');");

        Execute(
            connection,
            "UPDATE aux.items SET value = 'selected' ORDER BY rank ASC NULLS LAST LIMIT 1;");
        ReadRows(connection, "SELECT id FROM aux.items WHERE value = 'selected';")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));

        var crossDatabaseOrder = () => Execute(
            connection,
            "UPDATE aux.items SET value = 'rejected' "
            + "ORDER BY (SELECT count(*) FROM main.items) LIMIT 1;");
        crossDatabaseOrder.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Cross-database statements are not supported by managed ATTACH;*");
        ReadRows(connection, "SELECT count(*) FROM aux.items WHERE value = 'rejected';")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));

        Execute(connection, "BEGIN;");
        Execute(connection, "UPDATE main.items SET value = 'pending' ORDER BY id LIMIT 1;");
        var secondDatabaseWrite = () => Execute(connection, "DELETE FROM aux.items ORDER BY id LIMIT 1;");
        secondDatabaseWrite.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*cannot modify more than one database*atomically*");
        Execute(connection, "ROLLBACK;");

        ReadRows(connection, "SELECT value FROM main.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("main"));
        ReadRows(connection, "SELECT count(*) FROM aux.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void AttachedUpdateMayReadMainFromItsWherePredicate()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-cross-read-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE 'attach-cross-read-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE main.selector(x INTEGER);");
        Execute(connection, "INSERT INTO main.selector VALUES (1);");
        Execute(connection, "CREATE TABLE aux.t1(id INTEGER PRIMARY KEY, val REAL UNIQUE, data INTEGER);");
        Execute(connection, "INSERT INTO aux.t1 VALUES (1, 10.0, 100), (2, 20.0, 200), (3, 30.0, 300);");

        Execute(connection, "BEGIN;");
        Execute(
            connection,
            "UPDATE aux.t1 SET id = 20, data = 555 "
                + "WHERE id = 3 AND NOT EXISTS (SELECT x FROM selector WHERE x > 10);");
        Execute(connection, "COMMIT;");

        AssertRows(
            ReadRows(connection, "SELECT id, val, data FROM aux.t1 ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Real(10), SqlValue.Integer(100)],
            [SqlValue.Integer(2), SqlValue.Real(20), SqlValue.Integer(200)],
            [SqlValue.Integer(20), SqlValue.Real(30), SqlValue.Integer(555)]);
    }

    [Test]
    public void DirectManagedAttachRejectsUnsafeAliasesUnknownSchemasAndCrossDatabaseQueries()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-errors-main.db", fileSystem);
        using var connection = main.Connect();

        Execute(connection, "ATTACH DATABASE ':memory:' AS memory;");
        Execute(connection, "DETACH DATABASE memory;");

        var key = () => Execute(connection, "ATTACH DATABASE 'attach-errors-key.db' AS encrypted KEY '00';");
        key.Should().Throw<EmbeddedSqlException>().WithMessage("*encrypted primary database*");

        Execute(connection, "ATTACH DATABASE 'attach-errors-secondary.db' AS aux;");
        var duplicateAlias = () => Execute(connection, "ATTACH DATABASE 'unused-secondary.db' AS aux;");
        duplicateAlias.Should().Throw<EmbeddedSqlException>().WithMessage("database aux is already in use");
        fileSystem.FileExists("unused-secondary.db").Should().BeFalse();

        var duplicateFile = () => Execute(connection, "ATTACH DATABASE 'attach-errors-secondary.db' AS other;");
        duplicateFile.Should().Throw<EmbeddedSqlException>().WithMessage("database file is already attached");

        var missingDetach = () => Execute(connection, "DETACH DATABASE absent;");
        missingDetach.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: absent");

        var missingSchema = () => ReadRows(connection, "SELECT * FROM absent.items;");
        missingSchema.Should().Throw<EmbeddedSqlException>().WithMessage("no such database: absent");

        Execute(connection, "CREATE TABLE main_items(id INTEGER PRIMARY KEY);");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY);");
        var crossDatabase = () => ReadRows(
            connection,
            "SELECT * FROM main.main_items JOIN aux.items ON 1 = 1;");
        crossDatabase.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Cross-database statements are not supported by managed ATTACH;*");

        var temporary = () => ReadRows(connection, "SELECT * FROM temp.items;");
        temporary.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: items");
    }

    [Test]
    public void DirectManagedAttachTransactionsCommitOneDatabaseAndRejectASecondWriteBeforeExecution()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var seed = EmbeddedDatabase.OpenFile("attach-readonly-main.db", fileSystem))
        using (var connection = seed.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'attach-readonly-secondary.db' AS aux;");
            Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO aux.items VALUES (1);");
            Execute(connection, "CREATE TABLE main.items(id INTEGER PRIMARY KEY);");

            Execute(connection, "BEGIN;");
            Execute(connection, "INSERT INTO aux.items VALUES (2);");
            Execute(connection, "SAVEPOINT nested;");
            Execute(connection, "INSERT INTO aux.items VALUES (3);");
            Execute(connection, "ROLLBACK TO nested;");
            Execute(connection, "RELEASE nested;");
            Execute(connection, "COMMIT;");
            ReadRows(connection, "SELECT id FROM aux.items ORDER BY id;").Should().HaveCount(2);

            Execute(connection, "BEGIN;");
            var failedStatement = () => Execute(
                connection,
                "INSERT INTO aux.items VALUES (30) RETURNING missing_column;");
            failedStatement.Should().Throw<EmbeddedSqlException>().WithMessage("no such column: missing_column");
            Execute(connection, "INSERT INTO aux.items VALUES (31);");
            Execute(connection, "COMMIT;");
            ReadRows(connection, "SELECT COUNT(*) FROM aux.items WHERE id = 30;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
            ReadRows(connection, "SELECT COUNT(*) FROM aux.items WHERE id = 31;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
            Execute(connection, "DELETE FROM aux.items WHERE id = 31;");

            Execute(connection, "BEGIN;");
            Execute(connection, "INSERT INTO main.items VALUES (1);");
            var secondDatabaseWrite = () => Execute(connection, "INSERT INTO aux.items VALUES (4);");
            secondDatabaseWrite.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*cannot modify more than one database*atomically*");
            ReadRows(connection, "SELECT COUNT(*) FROM aux.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));
            Execute(connection, "ROLLBACK;");
            ReadRows(connection, "SELECT COUNT(*) FROM main.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));

            connection.RegisterScalarFunction(
                "reentrant_main_write",
                0,
                _ =>
                {
                    Execute(connection, "INSERT INTO main.items VALUES (20);");
                    return SqlValue.Integer(20);
                });
            Execute(connection, "BEGIN;");
            var reentrantWrite = () => Execute(connection, "INSERT INTO aux.items VALUES (reentrant_main_write());");
            reentrantWrite.Should().Throw<EmbeddedSqlException>().WithMessage("*reentrant writes*");
            Execute(connection, "ROLLBACK;");
            ReadRows(connection, "SELECT COUNT(*) FROM main.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
            var autocommitReentrantWrite = () => Execute(
                connection,
                "INSERT INTO aux.items VALUES (reentrant_main_write());");
            autocommitReentrantWrite.Should().Throw<EmbeddedSqlException>().WithMessage("*reentrant writes*");
            ReadRows(connection, "SELECT COUNT(*) FROM main.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));

            connection.RegisterScalarFunction(
                "reentrant_commit",
                0,
                _ =>
                {
                    Execute(connection, "COMMIT;");
                    return SqlValue.Integer(21);
                });
            Execute(connection, "BEGIN;");
            var reentrantCommit = () => Execute(connection, "INSERT INTO aux.items VALUES (reentrant_commit());");
            reentrantCommit.Should().Throw<EmbeddedSqlException>().WithMessage("*cannot change transaction*");
            Execute(connection, "ROLLBACK;");

            using (var sibling = seed.Connect())
            {
                Execute(connection, "BEGIN;");
                // The competing write has to land before this transaction takes its
                // write lock at its own first write.
                Execute(sibling, "INSERT INTO main.items VALUES (9);");
                Execute(connection, "INSERT INTO main.items VALUES (5);");
                var staleCommit = () => Execute(connection, "COMMIT;");
                staleCommit.Should().Throw<EmbeddedSqlException>().WithMessage("database is locked");
                Execute(connection, "ROLLBACK;");
            }

            AssertRows(
                ReadRows(connection, "SELECT id FROM main.items ORDER BY id;"),
                [SqlValue.Integer(9)]);

            Execute(connection, "BEGIN;");
            var detachInTransaction = () => Execute(connection, "DETACH aux;");
            detachInTransaction.Should().Throw<EmbeddedSqlException>().WithMessage("database aux is locked");
            var attachInTransaction = () => Execute(
                connection,
                "ATTACH DATABASE 'attach-other-secondary.db' AS other;");
            attachInTransaction.Should().Throw<EmbeddedSqlException>().WithMessage("*not supported inside a transaction*");
            Execute(connection, "ROLLBACK;");
            Execute(connection, "DETACH aux;");
        }

        using var readOnlyMain = EmbeddedDatabase.OpenFile("attach-readonly-main.db", fileSystem, readOnly: true);
        using var readOnlyConnection = readOnlyMain.Connect();
        Execute(readOnlyConnection, "ATTACH DATABASE 'attach-readonly-secondary.db' AS aux;");
        AssertRows(
            ReadRows(readOnlyConnection, "SELECT id FROM aux.items ORDER BY id;"),
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)]);

        var writeAttached = () => Execute(readOnlyConnection, "INSERT INTO aux.items VALUES (2);");
        writeAttached.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");
    }

    [Test]
    public void AttachedForeignKeysStayWithinTheirDatabaseAndPreserveSingleWriterSafety()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("attach-fk-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "ATTACH DATABASE 'attach-fk-aux.db' AS aux;");
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "CREATE TABLE main.parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b));");
            Execute(connection, "CREATE TABLE aux.parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b));");
            Execute(
                connection,
                "CREATE TABLE aux.child(a INTEGER, b INTEGER, "
                    + "FOREIGN KEY(a, b) REFERENCES parent ON UPDATE CASCADE ON DELETE CASCADE);");
            Execute(connection, "INSERT INTO main.parent VALUES (1, 2);");
            Execute(connection, "INSERT INTO aux.parent VALUES (1, 2);");
            Execute(connection, "INSERT INTO aux.child VALUES (1, 2);");

            Action crossDatabaseReference = () => Execute(
                connection,
                "CREATE TABLE aux.cross_database(a INTEGER REFERENCES main.parent(a));");
            crossDatabaseReference.Should().Throw<EmbeddedSqlException>()
                .WithMessage("Schema-qualified foreign keys are not supported*");
            ReadRows(connection, "SELECT COUNT(*) FROM aux.sqlite_schema WHERE name = 'cross_database';")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));

            Execute(connection, "BEGIN;");
            Execute(connection, "UPDATE aux.parent SET a = 3, b = 4;");
            ReadRows(connection, "SELECT a, b FROM aux.child;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
            Action secondDatabaseWrite = () => Execute(connection, "DELETE FROM main.parent;");
            secondDatabaseWrite.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*cannot modify more than one database*atomically*");
            Execute(connection, "COMMIT;");
            Execute(connection, "DETACH aux;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("attach-fk-aux.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        ReadRows(reopenedConnection, "SELECT a, b FROM child;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3), SqlValue.Integer(4));
        Execute(reopenedConnection, "DELETE FROM parent;");
        ReadRows(reopenedConnection, "SELECT COUNT(*) FROM child;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void DirectManagedAttachEvaluatesFilenameExpressionsAndHonorsUriReadOnlyMode()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-expression-main.db", fileSystem);
        using var connection = main.Connect();

        using (var attach = connection.Prepare("ATTACH DATABASE ?1 || '.db' AS aux;"))
        {
            attach.Bind(1, SqlValue.Text("attach-expression-secondary"));
            attach.Step().Should().Be(StatementStepResult.Done);
        }
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY);");
        Execute(connection, "INSERT INTO aux.items VALUES (1);");
        Execute(connection, "DETACH aux;");

        Execute(connection, "ATTACH DATABASE 'file:attach-expression-secondary.db?mode=ro' AS aux;");
        ReadRows(connection, "SELECT id FROM aux.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
        var write = () => Execute(connection, "INSERT INTO aux.items VALUES (2);");
        write.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");
        Execute(connection, "DETACH aux;");

        // Turso-known URI options beyond mode are accepted as no-ops (OpenOptions::parse).
        Execute(connection, "ATTACH DATABASE 'file:attach-expression-secondary.db?cache=shared' AS aux;");
        ReadRows(connection, "SELECT id FROM aux.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
        Execute(connection, "DETACH aux;");
        Execute(
            connection,
            "ATTACH DATABASE 'file:attach-expression-secondary.db?immutable=1&vfs=unix&modeof=.' AS aux;");
        ReadRows(connection, "SELECT id FROM aux.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1));
        Execute(connection, "DETACH aux;");

        Execute(connection, "ATTACH DATABASE 'attach-case.db' AS lower_case;");
        Execute(connection, "ATTACH DATABASE 'ATTACH-CASE.db' AS upper_case;");
        ReadRows(connection, "PRAGMA database_list;").Should().HaveCount(3);
    }

    [Test]
    public void FreshAttachInheritsMainPageSizeAndInitializedMismatchIsRejected()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var seed = EmbeddedDatabase.OpenFile(
                   "attach-page-seed.db",
                   fileSystem,
                   initialPageSize: 8192))
        using (var seedConnection = seed.Connect())
        {
            Execute(seedConnection, "CREATE TABLE items(id INTEGER PRIMARY KEY);");
            Execute(seedConnection, "INSERT INTO items VALUES (1);");
        }

        using var main = EmbeddedDatabase.OpenFile(
            "attach-page-main.db",
            fileSystem,
            initialPageSize: 4096);
        using var connection = main.Connect();
        Execute(connection, "CREATE TABLE main_items(id INTEGER PRIMARY KEY);");

        Execute(connection, "ATTACH DATABASE 'attach-page-fresh.db' AS fresh;");
        ReadInteger(connection, "PRAGMA fresh.page_size;").Should().Be(4096);
        Execute(connection, "CREATE TABLE fresh.items(id INTEGER PRIMARY KEY);");
        Execute(connection, "DETACH fresh;");

        var mismatch = () => Execute(connection, "ATTACH DATABASE 'attach-page-seed.db' AS bad;");
        mismatch.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*page size mismatch*");
    }

    [Test]
    public void ExplainAndExplainQueryPlanAcceptAttachedSchemaQualification()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-explain-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "ATTACH DATABASE 'attach-explain-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "INSERT INTO aux.items VALUES (1, 'one');");

        var eqp = ReadRows(connection, "EXPLAIN QUERY PLAN SELECT value FROM aux.items WHERE id = 1;");
        eqp.Should().NotBeEmpty();

        // EXPLAIN may still refuse evaluator-only shapes; routing must not reject schema.
        Action explain = () => ReadRows(connection, "EXPLAIN SELECT value FROM aux.items WHERE id = 1;");
        try
        {
            explain();
        }
        catch (EmbeddedSqlException ex)
        {
            ex.Message.Should().NotContain("schema-qualified");
            ex.Message.Should().NotContain("not supported by managed ATTACH");
        }
    }

    private static long ReadInteger(EmbeddedConnection connection, string sql)
    {
        var rows = ReadRows(connection, sql);
        rows.Should().ContainSingle();
        return rows[0][0].AsInteger();
    }

    [Test]
    public void DirectManagedAttachRejectsCaseVariantsOnCaseInsensitivePhysicalPlatforms()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Assert.Ignore("This regression exercises case-insensitive physical file identity.");

        var directory = Path.Combine(Path.GetTempPath(), $"managed-attach-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var mainPath = Path.Combine(directory, "main.db");
        var attachedPath = Path.Combine(directory, "attached.db");
        try
        {
            using var main = EmbeddedDatabase.OpenFile(mainPath);
            using var connection = main.Connect();

            var duplicateMain = () => Execute(
                connection,
                $"ATTACH DATABASE '{Path.Combine(directory, "MAIN.DB")}' AS duplicate_main;");
            duplicateMain.Should().Throw<EmbeddedSqlException>().WithMessage("database file is already open as main");

            Execute(connection, $"ATTACH DATABASE '{attachedPath}' AS aux;");
            var duplicateAttachment = () => Execute(
                connection,
                $"ATTACH DATABASE '{Path.Combine(directory, "ATTACHED.DB")}' AS duplicate_aux;");
            duplicateAttachment.Should().Throw<EmbeddedSqlException>().WithMessage("database file is already attached");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void DirectManagedAttachEnforcesAliasAndTenDatabaseBoundaries()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-limit-main.db", fileSystem);
        using var connection = main.Connect();

        for (var index = 0; index < 10; index++)
            Execute(connection, $"ATTACH DATABASE 'attach-limit-{index}.db' AS db{index};");

        ReadRows(connection, "PRAGMA database_list;").Should().HaveCount(11);
        var tooMany = () => Execute(connection, "ATTACH DATABASE 'attach-limit-overflow.db' AS overflow;");
        tooMany.Should().Throw<EmbeddedSqlException>().WithMessage("too many attached databases - maximum 10");
        fileSystem.FileExists("attach-limit-overflow.db").Should().BeFalse();

        var mainAlias = () => Execute(connection, "ATTACH DATABASE 'unused-main.db' AS main;");
        mainAlias.Should().Throw<EmbeddedSqlException>().WithMessage("cannot attach database as main");
        var tempAlias = () => Execute(connection, "ATTACH DATABASE 'unused-temp.db' AS temp;");
        tempAlias.Should().Throw<EmbeddedSqlException>().WithMessage("cannot attach database as temp");

        Execute(connection, "CREATE TABLE db0.only_attached(value INTEGER);");
        Execute(connection, "CREATE TABLE main.index_owner(value INTEGER);");
        Execute(connection, "CREATE INDEX only_attached ON index_owner(value);");
        Execute(connection, "DROP TABLE only_attached;");
        var dropped = () => ReadRows(connection, "SELECT * FROM db0.only_attached;");
        dropped.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: only_attached");

        Execute(connection, "CREATE TABLE db0.indexed(value INTEGER);");
        Execute(connection, "CREATE INDEX db0.indexed_value ON indexed(value);");
        Execute(connection, "DROP INDEX indexed_value;");
    }

    [Test]
    public void DirectManagedAttachInheritsEncryptionAndSupportsSameCipherKeyOverride()
    {
        var inner = new InMemoryFileSystem();
        using var mainEncryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var encryptedFileSystem = new AhtolaEncryptionFileSystem(inner, mainEncryption);
        using var main = EmbeddedDatabase.OpenFile("attach-encrypted-main.db", encryptedFileSystem);
        using var connection = main.Connect();

        Execute(connection, "ATTACH DATABASE 'attach-encrypted-inherited.db' AS inherited;");
        Execute(connection, "CREATE TABLE inherited.items(value TEXT);");
        Execute(connection, "INSERT INTO inherited.items VALUES ('inherited');");
        Execute(connection, "DETACH inherited;");
        Execute(
            connection,
            $"ATTACH DATABASE 'attach-encrypted-override.db' AS overridden KEY '{OtherAes256Key}';");
        Execute(connection, "CREATE TABLE overridden.items(value TEXT);");
        Execute(connection, "INSERT INTO overridden.items VALUES ('overridden');");
        Execute(connection, "DETACH overridden;");

        var wrongInheritedKey = () => Execute(
            connection,
            $"ATTACH DATABASE 'attach-encrypted-inherited.db' AS wrong KEY '{OtherAes256Key}';");
        wrongInheritedKey.Should().Throw<InvalidDataException>().WithMessage("*failed authentication*");

        Execute(
            connection,
            $"ATTACH DATABASE 'attach-encrypted-override.db' AS overridden KEY '{OtherAes256Key}';");
        ReadRows(connection, "SELECT value FROM overridden.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("overridden"));
    }

    [Test]
    public void ManagedProviderBlocksAttachAndDetachWhileAReaderIsActive()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-attach-reader-tests");
        Directory.CreateDirectory(directory);
        var mainPath = Path.Combine(directory, $"main-{Guid.NewGuid():N}.db");
        var attachedPath = Path.Combine(directory, $"attached-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new Ahtola.Data.Sqlite.SqliteConnection(
                $"Data Source={mainPath};Local Provider=Managed;Default Timeout=1");
            connection.Open();
            connection.ExecuteNonQuery($"ATTACH DATABASE '{attachedPath}' AS aux;");

            using (var reader = connection.ExecuteReader("SELECT 1;"))
            {
                reader.Read().Should().BeTrue();
                var detach = () => connection.ExecuteNonQuery("-- attachment lifecycle\nDETACH aux;");
                detach.Should().Throw<Ahtola.Data.Sqlite.SqliteException>().Which.SqliteErrorCode.Should().Be(5);
            }

            connection.ExecuteNonQuery("DETACH aux;");
            using (var reader = connection.ExecuteReader("SELECT 1;"))
            {
                reader.Read().Should().BeTrue();
                var attach = () => connection.ExecuteNonQuery($"ATTACH DATABASE '{attachedPath}' AS aux;");
                attach.Should().Throw<Ahtola.Data.Sqlite.SqliteException>().Which.SqliteErrorCode.Should().Be(5);
            }

            connection.Close();
            using var readOnly = new Ahtola.Data.Sqlite.SqliteConnection(
                $"Data Source={mainPath};Local Provider=Managed;Mode=ReadOnly");
            readOnly.Open();
            var attachedUri = new Uri(attachedPath).AbsoluteUri + "?mode=ro";
            readOnly.ExecuteNonQuery($"ATTACH DATABASE '{attachedUri}' AS aux;");
            readOnly.ExecuteScalar<long>("SELECT COUNT(*) FROM aux.sqlite_schema;").Should().Be(0);
        }
        finally
        {
            foreach (var path in new[] { mainPath, attachedPath })
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var candidate = path + suffix;
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
            }
        }
    }

    [Test]
    public void DirectManagedAttachKeepsQueryOnlyDynamicAndSharesConnectionRegistries()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var main = EmbeddedDatabase.OpenFile("attach-runtime-main.db", fileSystem))
        using (var connection = main.Connect())
        {
            connection.RegisterScalarFunction(
                "managed_double",
                1,
                values => SqlValue.Integer(values[0].AsInteger() * 2));
            connection.RegisterAggregateFunction(
                "managed_product",
                1,
                SqlValue.Integer(1),
                (aggregate, values) => SqlValue.Integer(aggregate.AsInteger() * values[0].AsInteger()),
                aggregate => aggregate);
            connection.RegisterCollation(
                "managed_reverse",
                (left, right) => string.CompareOrdinal(right, left));

            Execute(connection, "PRAGMA query_only = ON;");
            Execute(connection, "ATTACH DATABASE 'attach-runtime-existing.db' AS existing;");
            var blocked = () => Execute(connection, "CREATE TABLE existing.items(value INTEGER);");
            blocked.Should().Throw<EmbeddedSqlException>().WithMessage("attempt to write a readonly database");

            Execute(connection, "PRAGMA query_only = OFF;");
            Execute(connection, "CREATE TABLE existing.items(value INTEGER);");
            Execute(connection, "INSERT INTO existing.items VALUES (1), (2), (3);");
            Execute(connection, "CREATE TABLE existing.names(value TEXT);");
            Execute(connection, "INSERT INTO existing.names VALUES ('a'), ('b'), ('c');");
            AssertRows(
                ReadRows(connection, "SELECT managed_double(value) FROM existing.items ORDER BY value;"),
                [SqlValue.Integer(2)],
                [SqlValue.Integer(4)],
                [SqlValue.Integer(6)]);
            ReadRows(connection, "SELECT managed_product(value) FROM existing.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(6));
            AssertRows(
                ReadRows(connection, "SELECT value FROM existing.names ORDER BY value COLLATE managed_reverse;"),
                [SqlValue.Text("c")],
                [SqlValue.Text("b")],
                [SqlValue.Text("a")]);

            connection.RegisterScalarFunction(
                "managed_triple",
                1,
                values => SqlValue.Integer(values[0].AsInteger() * 3));
            connection.RegisterCollation(
                "managed_nocase_reverse",
                (left, right) => string.CompareOrdinal(right, left));
            AssertRows(
                ReadRows(connection, "SELECT managed_triple(value) FROM existing.items ORDER BY value;"),
                [SqlValue.Integer(3)],
                [SqlValue.Integer(6)],
                [SqlValue.Integer(9)]);
            AssertRows(
                ReadRows(connection, "SELECT value FROM existing.names ORDER BY value COLLATE managed_nocase_reverse;"),
                [SqlValue.Text("c")],
                [SqlValue.Text("b")],
                [SqlValue.Text("a")]);

            Execute(connection, "ATTACH DATABASE 'attach-runtime-future.db' AS future;");
            Execute(connection, "CREATE TABLE future.items(value INTEGER);");
            Execute(connection, "INSERT INTO future.items VALUES (1), (2);");
            Execute(connection, "CREATE TABLE future.names(value TEXT);");
            Execute(connection, "INSERT INTO future.names VALUES ('a'), ('b');");
            AssertRows(
                ReadRows(connection, "SELECT managed_double(value), managed_triple(value) FROM future.items ORDER BY value;"),
                [SqlValue.Integer(2), SqlValue.Integer(3)],
                [SqlValue.Integer(4), SqlValue.Integer(6)]);
            ReadRows(connection, "SELECT managed_product(value) FROM future.items;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));
            AssertRows(
                ReadRows(connection, "SELECT value FROM future.names ORDER BY value COLLATE managed_nocase_reverse;"),
                [SqlValue.Text("b")],
                [SqlValue.Text("a")]);

            Execute(connection, "DETACH existing;");
            Execute(connection, "DETACH future;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("attach-runtime-existing.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        AssertRows(
            ReadRows(reopenedConnection, "SELECT value FROM items ORDER BY value;"),
            [SqlValue.Integer(1)],
            [SqlValue.Integer(2)],
            [SqlValue.Integer(3)]);
    }

    [Test]
    public void DirectManagedAttachEnforcesTheTenDatabaseLimitAndReusesDetachedCapacity()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-limit-main.db", fileSystem);
        using var connection = main.Connect();

        for (var index = 0; index < 10; index++)
            Execute(connection, $"ATTACH DATABASE 'attach-limit-{index}.db' AS db{index};");

        ReadRows(connection, "PRAGMA database_list;").Should().HaveCount(11);
        var overLimit = () => Execute(connection, "ATTACH DATABASE 'attach-limit-overflow.db' AS overflow;");
        overLimit.Should().Throw<EmbeddedSqlException>().WithMessage("too many attached databases - maximum 10");
        fileSystem.FileExists("attach-limit-overflow.db").Should().BeFalse();

        Execute(connection, "DETACH DATABASE db4;");
        Execute(connection, "ATTACH DATABASE 'attach-limit-replacement.db' AS replacement;");
        Execute(connection, "CREATE TABLE replacement.items(value TEXT);");
        Execute(connection, "INSERT INTO replacement.items VALUES ('persisted');");
        Execute(connection, "DETACH DATABASE replacement;");

        using var reopened = EmbeddedDatabase.OpenFile("attach-limit-replacement.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT value FROM items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("persisted"));
    }

    [Test]
    public void DirectManagedAttachRoutesSchemaQualifiedCteDmlAndRejectsCrossDatabaseCteDmlAtomically()
    {
        var fileSystem = new InMemoryFileSystem();
        using var main = EmbeddedDatabase.OpenFile("attach-cte-main.db", fileSystem);
        using var connection = main.Connect();
        Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");
        Execute(connection, "ATTACH DATABASE 'attach-cte-aux.db' AS aux;");
        Execute(connection, "CREATE TABLE aux.items(id INTEGER PRIMARY KEY, value TEXT);");

        Execute(connection, "INSERT INTO aux.items VALUES (1, 'aux');");
        Execute(connection, "UPDATE aux.items SET value = 'updated' WHERE id = 1;");
        Execute(connection, """
            WITH selected(id) AS (SELECT 2)
            INSERT INTO items SELECT id, 'main' FROM selected;
            """);

        Execute(connection, """
            WITH selected(id) AS (SELECT 3)
            INSERT INTO aux.items SELECT id, 'attached' FROM selected;
            """);

        var crossDatabaseSource = () => Execute(connection, """
            WITH selected(id) AS (SELECT id FROM aux.items)
            INSERT INTO items SELECT id, 'rejected' FROM selected;
            """);
        crossDatabaseSource.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Cross-database statements are not supported by managed ATTACH;*");

        ReadRows(connection, "SELECT id, value FROM main.items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2), SqlValue.Text("main"));
        AssertRows(
            ReadRows(connection, "SELECT id, value FROM aux.items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("updated")],
            [SqlValue.Integer(3), SqlValue.Text("attached")]);
        Execute(connection, "DETACH DATABASE aux;");

        using var reopened = EmbeddedDatabase.OpenFile("attach-cte-aux.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        AssertRows(
            ReadRows(reopenedConnection, "SELECT id, value FROM items ORDER BY id;"),
            [SqlValue.Integer(1), SqlValue.Text("updated")],
            [SqlValue.Integer(3), SqlValue.Text("attached")]);
    }

    [Test]
    public void EncryptedManagedAttachInheritsTheMainKeyAndRejectsIncompatibleFilesAtomically()
    {
        var fileSystem = new InMemoryFileSystem();
        SeedPlaintextDatabase(fileSystem, "attach-encrypted-plaintext.db");
        SeedEncryptedDatabase(fileSystem, "attach-encrypted-other-key.db", OtherAes256Key);

        using (var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key))
        using (var encryptedFileSystem = new AhtolaEncryptionFileSystem(fileSystem, encryption))
        using (var main = EmbeddedDatabase.OpenFile("attach-encrypted-main.db", encryptedFileSystem))
        using (var connection = main.Connect())
        {
            Execute(connection, "CREATE TABLE durable(value TEXT);");
            Execute(connection, "INSERT INTO durable VALUES ('main');");
            Execute(connection, "ATTACH DATABASE 'attach-encrypted-aux.db' AS aux;");
            Execute(connection, "CREATE TABLE aux.items(value TEXT);");
            Execute(connection, "INSERT INTO aux.items VALUES ('encrypted');");
            Execute(connection, "DETACH DATABASE aux;");

            Assert.Throws<InvalidDataException>(
                    () => Execute(connection, "ATTACH DATABASE 'attach-encrypted-plaintext.db' AS plaintext;"))!
                .Message.Should().Contain("Plaintext fallback");
            Assert.Throws<InvalidDataException>(
                    () => Execute(connection, "ATTACH DATABASE 'attach-encrypted-other-key.db' AS other_key;"))!
                .Message.Should().Contain("failed authentication");
            ReadRows(connection, "PRAGMA database_list;").Should().ContainSingle();
            ReadRows(connection, "SELECT value FROM durable;")
                .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("main"));
        }

        Assert.Throws<InvalidDataException>(
            () => EmbeddedDatabase.OpenFile("attach-encrypted-aux.db", fileSystem))!
            .Message.Should().Contain("encrypted");

        using var reopenEncryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key);
        using var reopenedFileSystem = new AhtolaEncryptionFileSystem(fileSystem, reopenEncryption);
        using var reopened = EmbeddedDatabase.OpenFile("attach-encrypted-aux.db", reopenedFileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT value FROM items;")
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Text("encrypted"));
    }

    private static void SeedPlaintextDatabase(IFileSystem fileSystem, string path)
    {
        using var database = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(value TEXT);");
    }

    private static void SeedEncryptedDatabase(IFileSystem fileSystem, string path, string key)
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, key);
        using var encryptedFileSystem = new AhtolaEncryptionFileSystem(fileSystem, encryption);
        using var database = EmbeddedDatabase.OpenFile(path, encryptedFileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE items(value TEXT);");
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
        {
            actual[rowIndex].Length.Should().Be(expected[rowIndex].Length);
            for (var columnIndex = 0; columnIndex < expected[rowIndex].Length; columnIndex++)
                actual[rowIndex][columnIndex].Should().Be(expected[rowIndex][columnIndex]);
        }
    }
}
