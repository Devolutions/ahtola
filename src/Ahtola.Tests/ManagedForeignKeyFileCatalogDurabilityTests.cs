using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedForeignKeyFileCatalogDurabilityTests
{
    [Test]
    public void SupportedForeignKeysRoundTripAndEnforceChildAndParentMutationsAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-file-catalog-roundtrip.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, code TEXT);");
            Execute(connection, "CREATE UNIQUE INDEX parent_code ON parent(code);");
            Execute(
                connection,
                "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id), parent_code TEXT, FOREIGN KEY(parent_code) REFERENCES parent(code));");
            Execute(connection, "INSERT INTO parent VALUES (1, 'one'), (2, 'two');");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name = 'child';")
                .Should().Contain("REFERENCES parent(id)")
                .And.Contain("REFERENCES parent(code)");

            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "INSERT INTO child VALUES (10, 1, 'one');");
            Execute(connection, "UPDATE parent SET code = 'two-updated' WHERE id = 2;");
            Execute(connection, "UPDATE child SET parent_id = 2, parent_code = 'two-updated' WHERE id = 10;");

            Action invalidChildInsert = () => Execute(connection, "INSERT INTO child VALUES (11, 999, 'missing');");
            invalidChildInsert.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            Action invalidChildUpdate = () => Execute(connection, "UPDATE child SET parent_id = 999 WHERE id = 10;");
            invalidChildUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            Action invalidParentUpdate = () => Execute(connection, "UPDATE parent SET id = 3 WHERE id = 2;");
            invalidParentUpdate.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            Action invalidParentDelete = () => Execute(connection, "DELETE FROM parent WHERE id = 2;");
            invalidParentDelete.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");

            ScalarInteger(connection, "SELECT COUNT(*) FROM child;").Should().Be(1);
            ScalarInteger(connection, "SELECT parent_id FROM child WHERE id = 10;").Should().Be(2);
            ScalarInteger(connection, "SELECT COUNT(*) FROM parent;").Should().Be(2);
        }

        using var verifiedReopen = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var verifiedConnection = verifiedReopen.Connect();
        Execute(verifiedConnection, "PRAGMA foreign_keys = ON;");
        ScalarText(verifiedConnection, "SELECT code FROM parent WHERE id = 2;").Should().Be("two-updated");
        ScalarInteger(verifiedConnection, "SELECT parent_id FROM child WHERE id = 10;").Should().Be(2);
    }

    [Test]
    public void AddedColumnForeignKeyEnforcesBeforeAndAfterReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "added-column-foreign-key.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "CREATE TABLE parent(code TEXT UNIQUE);");
            Execute(connection, "CREATE TABLE child(id INTEGER);");
            Execute(
                connection,
                "ALTER TABLE child ADD COLUMN code TEXT REFERENCES parent(code) "
                    + "ON DELETE RESTRICT ON UPDATE RESTRICT;");
            ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name = 'child';")
                .Should().Contain("REFERENCES parent(code)");

            Execute(connection, "INSERT INTO parent VALUES ('ok');");
            Execute(connection, "INSERT INTO child(id, code) VALUES (1, 'ok'), (2, NULL);");
            Action invalidChild = () => Execute(connection, "INSERT INTO child(id, code) VALUES (3, 'missing');");
            invalidChild.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM child;").Should().Be(2);
        Value(reopenedConnection, "SELECT code FROM child WHERE id = 1;").Should().Be(SqlValue.Text("ok"));
        Value(reopenedConnection, "SELECT code FROM child WHERE id = 2;").Should().Be(SqlValue.Null);
        Action reopenedInvalidChild = () => Execute(
            reopenedConnection,
            "INSERT INTO child(id, code) VALUES (3, 'missing');");
        reopenedInvalidChild.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
    }

    [Test]
    public void CompositeActionsAndDeferralRoundTripAcrossReopenAndFailedCommitRepair()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-full-roundtrip.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b));");
            Execute(
                connection,
                "CREATE TABLE child(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                    + "FOREIGN KEY(a, b) REFERENCES parent "
                    + "ON UPDATE CASCADE ON DELETE SET NULL MATCH FULL DEFERRABLE INITIALLY DEFERRED);");
            Execute(connection, "INSERT INTO parent VALUES (1, 2);");
            Execute(connection, "INSERT INTO child VALUES (10, 1, 2);");
        }

        using (var reopened = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = reopened.Connect())
        {
            var schema = ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name = 'child';");
            schema.Should().Contain("FOREIGN KEY(a, b)")
                .And.Contain("REFERENCES parent")
                .And.Contain("ON DELETE SET NULL")
                .And.Contain("ON UPDATE CASCADE")
                .And.Contain("MATCH FULL")
                .And.Contain("DEFERRABLE INITIALLY DEFERRED");

            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "UPDATE parent SET a = 3, b = 4;");
            ScalarInteger(connection, "SELECT a FROM child WHERE id = 10;").Should().Be(3);
            ScalarInteger(connection, "SELECT b FROM child WHERE id = 10;").Should().Be(4);

            Execute(connection, "BEGIN;");
            Execute(connection, "INSERT INTO child VALUES (11, 9, 9);");
            Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "COMMIT;"))!
                .Message.Should().Be("FOREIGN KEY constraint failed");
            Execute(connection, "INSERT INTO parent VALUES (9, 9);");
            Execute(connection, "COMMIT;");

            Execute(connection, "DELETE FROM parent WHERE a = 3 AND b = 4;");
            Value(connection, "SELECT a FROM child WHERE id = 10;").Should().Be(SqlValue.Null);
            Value(connection, "SELECT b FROM child WHERE id = 10;").Should().Be(SqlValue.Null);
        }

        using var verified = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var verifiedConnection = verified.Connect();
        Execute(verifiedConnection, "PRAGMA foreign_keys = ON;");
        ScalarInteger(verifiedConnection, "SELECT COUNT(*) FROM child;").Should().Be(2);
        Execute(verifiedConnection, "DELETE FROM parent WHERE a = 9 AND b = 9;");
        Value(verifiedConnection, "SELECT a FROM child WHERE id = 11;").Should().Be(SqlValue.Null);
    }

    [Test]
    public void LargeCompositeForeignKeyCatalogSurvivesInteriorPagesAndReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-large-catalog.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(a INTEGER, b INTEGER, PRIMARY KEY(a, b));");
            for (var index = 0; index < 48; index++)
            {
                Execute(
                    connection,
                    $"CREATE TABLE child_{index:D2}(id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, "
                        + $"CONSTRAINT fk_child_{index:D2}_parent FOREIGN KEY(a, b) REFERENCES parent "
                        + "ON UPDATE CASCADE ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED);");
            }
            Execute(connection, "INSERT INTO parent VALUES (1, 2);");
            Execute(connection, "INSERT INTO child_47 VALUES (47, 1, 2);");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table';").Should().Be(49);
        ScalarText(reopenedConnection, "SELECT sql FROM sqlite_schema WHERE name = 'child_47';")
            .Should().Contain("fk_child_47_parent")
            .And.Contain("ON UPDATE CASCADE");
        Execute(reopenedConnection, "UPDATE parent SET a = 3, b = 4;");
        ScalarInteger(reopenedConnection, "SELECT a FROM child_47;").Should().Be(3);
        ScalarInteger(reopenedConnection, "SELECT b FROM child_47;").Should().Be(4);
    }

    [Test]
    public void FailLimitedCascadePublishesOnlySqliteRetainedRowsAcrossReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-fail-limited-cascade.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "PRAGMA foreign_keys=ON;");
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY, priority INTEGER);");
            Execute(
                connection,
                "CREATE TABLE child(id INTEGER PRIMARY KEY, "
                    + "parent_id INTEGER REFERENCES parent(id) ON UPDATE CASCADE ON DELETE CASCADE);");
            Execute(connection, "CREATE INDEX parent_priority ON parent(priority DESC);");
            Execute(connection, "INSERT INTO parent VALUES (1, 1), (2, 2), (3, 3);");
            Execute(connection, "INSERT INTO child VALUES (10, 1), (20, 2), (30, 3);");

            Execute(
                connection,
                "UPDATE parent SET id=101 ORDER BY priority ASC NULLS LAST LIMIT 1;");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "INSERT OR FAIL INTO parent VALUES (4, 4), (2, 20), (5, 5);"))!
                .Message.Should().Contain("UNIQUE constraint failed");
            ScalarText(
                connection,
                "SELECT group_concat(id || ':' || priority, ',') "
                    + "FROM (SELECT id, priority FROM parent ORDER BY id);")
                .Should().Be("2:2,3:3,4:4,101:1");
            ScalarText(
                connection,
                "SELECT group_concat(id || ':' || parent_id, ',') "
                    + "FROM (SELECT id, parent_id FROM child ORDER BY id);")
                .Should().Be("10:101,20:2,30:3");

            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "INSERT OR FAIL INTO child VALUES (40, 101), (50, 404), (60, 101);"))!
                .Message.Should().Be("FOREIGN KEY constraint failed");
            ScalarInteger(connection, "SELECT COUNT(*) FROM child WHERE id >= 40;").Should().Be(0);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys=ON;");
        ScalarText(
            reopenedConnection,
            "SELECT group_concat(id || ':' || priority, ',') "
                + "FROM (SELECT id, priority FROM parent ORDER BY id);")
            .Should().Be("2:2,3:3,4:4,101:1");
        ScalarText(
            reopenedConnection,
            "SELECT group_concat(id || ':' || parent_id, ',') "
                + "FROM (SELECT id, parent_id FROM child ORDER BY id);")
            .Should().Be("10:101,20:2,30:3");
    }

    [Test]
    public void CorruptedPersistedForeignKeyCatalogFailsClosedDuringReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-file-catalog-corruption.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));");
        }

        CorruptForeignKeyKeyword(fileSystem, path);

        Assert.Throws<EmbeddedSqlException>(() => EmbeddedDatabase.OpenFile(path, fileSystem));
    }

    [Test]
    public void FailedForeignKeyCatalogPublicationRecoversThePriorCommittedCatalog()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "foreign-key-file-catalog-recovery.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
            Execute(connection, "INSERT INTO parent VALUES (1);");

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() =>
                Execute(connection, "CREATE TABLE child(parent_id INTEGER REFERENCES parent(id));"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM parent;").Should().Be(1);
        ScalarInteger(reopenedConnection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'child';").Should().Be(0);
    }

    [Test]
    public void FullForeignKeyFormsPersistWhileUnsafeQualifiedFormsAreNotPublished()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "foreign-key-file-catalog-gating.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE parent(id INTEGER, code INTEGER, PRIMARY KEY(id, code));");
            Execute(
                connection,
                "CREATE TABLE composite(a INTEGER, b INTEGER, "
                    + "FOREIGN KEY(a, b) REFERENCES parent ON UPDATE CASCADE ON DELETE SET NULL);");
            Execute(
                connection,
                "CREATE TABLE actions(parent_id INTEGER, parent_code INTEGER, "
                    + "FOREIGN KEY(parent_id, parent_code) REFERENCES parent(id, code) "
                    + "MATCH FULL DEFERRABLE INITIALLY DEFERRED);");
            Execute(
                connection,
                "CREATE TABLE unnamed_parent_column(parent_id INTEGER, parent_code INTEGER, "
                    + "FOREIGN KEY(parent_id, parent_code) REFERENCES parent);");
            Assert.Throws<EmbeddedSqlException>(() => Execute(
                connection,
                "CREATE TABLE qualified(parent_id INTEGER REFERENCES main.parent(id));"))!
                .Message.Should().Contain("Schema-qualified foreign keys are not supported");

            ScalarInteger(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table';").Should().Be(4);
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(
            reopenedConnection,
            "CREATE TABLE child(parent_id INTEGER, parent_code INTEGER, "
                + "FOREIGN KEY(parent_id, parent_code) REFERENCES parent);");
        Execute(reopenedConnection, "PRAGMA foreign_keys = ON;");
        Execute(reopenedConnection, "INSERT INTO parent VALUES (1, 10);");
        Execute(reopenedConnection, "INSERT INTO child VALUES (1, 10);");
        Assert.Throws<EmbeddedSqlException>(() => Execute(reopenedConnection, "INSERT INTO child VALUES (2, 20);"))!
            .Message.Should().Be("FOREIGN KEY constraint failed");
    }

    private static void CorruptForeignKeyKeyword(IFileSystem fileSystem, string path)
    {
        using var file = fileSystem.OpenFile(path, FileOpenMode.OpenExisting);
        var headerBytes = new byte[SqliteDatabaseHeader.Size];
        file.Read(0, headerBytes).Should().Be(headerBytes.Length);
        var header = SqliteDatabaseHeader.Parse(headerBytes);
        var page = new byte[header.PageSize];
        file.Read(0, page).Should().Be(page.Length);

        var schema = SqliteTableLeafPageView.Parse(page, header.UsableSpace, isFirstPage: true);
        var childCell = schema.Cells.Single(cell =>
        {
            var values = SqliteRecordCodec.Decode(cell.Cell.LocalPayload.Span, header.TextEncoding);
            return values[0].AsText() == "table" && values[1].AsText() == "child";
        });
        SqliteVarint.TryRead(page.AsSpan(childCell.Offset), out _, out var payloadLengthBytes).Should().BeTrue();
        SqliteVarint.TryRead(
            page.AsSpan(childCell.Offset + payloadLengthBytes),
            out _,
            out var rowIdBytes).Should().BeTrue();

        var payloadOffset = childCell.Offset + payloadLengthBytes + rowIdBytes;
        var payload = page.AsSpan(payloadOffset, childCell.Cell.LocalPayload.Length);
        var markerOffset = payload.IndexOf("REFERENCES"u8);
        markerOffset.Should().BeGreaterThanOrEqualTo(0);
        payload[markerOffset] = (byte)')';

        file.Write(0, page);
        file.FlushToDisk();
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0).AsInteger();
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static SqlValue Value(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static string ScalarText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0).AsText();
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }
}
