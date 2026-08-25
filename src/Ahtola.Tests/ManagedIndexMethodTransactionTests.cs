using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Proves that a managed method index participates in the engine's real transaction and savepoint
/// lifecycle: every DML shape maintains it, and nothing method-visible survives a rollback.
/// </summary>
public sealed class ManagedIndexMethodTransactionTests
{
    [Test]
    public void RollingBackATransactionDiscardsEveryMethodVisibleChange()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO docs VALUES (1, 'keeper', 'keeper body');");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs VALUES (2, 'ephemeral', 'ephemeral body');");
        Execute(connection, "UPDATE docs SET body = 'rewritten body' WHERE id = 1;");

        // Visible inside the transaction.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'ephemeral');")
            .Should().Equal(2);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'rewritten');")
            .Should().Equal(1);

        Execute(connection, "ROLLBACK;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'ephemeral');")
            .Should().BeEmpty("a rolled-back insert must leave no live term behind");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'rewritten');")
            .Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'keeper');")
            .Should().Equal(1);
    }

    [Test]
    public void CommittingATransactionPublishesEveryMethodVisibleChange()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs VALUES (1, 'committed', 'committed body');");
        Execute(connection, "COMMIT;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'committed');")
            .Should().Equal(1);
    }

    [Test]
    public void SavepointRollbackDiscardsOnlyTheInnerChanges()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs VALUES (1, 'outer', 'outer body');");
        Execute(connection, "SAVEPOINT sp;");
        Execute(connection, "INSERT INTO docs VALUES (2, 'inner', 'inner body');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'inner');").Should().Equal(2);

        Execute(connection, "ROLLBACK TO sp;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'inner');").Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'outer');").Should().Equal(1);

        Execute(connection, "RELEASE sp;");
        Execute(connection, "COMMIT;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'inner');").Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'outer');").Should().Equal(1);
    }

    [Test]
    public void AFailedStatementLeavesNoPartiallyIndexedRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT, CHECK (id < 3));");
        Execute(connection, CreateFtsIndex);

        // The third row violates the CHECK, so the whole statement rolls back.
        ShouldThrow(
            connection,
            "INSERT INTO docs VALUES (1, 'one', 'one body'), (2, 'two', 'two body'), (3, 'three', 'three body');");

        QueryIntegers(connection, "SELECT count(*) FROM docs;").Should().Equal(0);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'one');").Should().BeEmpty();
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'three');").Should().BeEmpty();
    }

    [Test]
    public void TriggerBodiesMaintainTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(connection, "CREATE TABLE inbox(id INTEGER PRIMARY KEY, text TEXT);");
        Execute(
            connection,
            """
            CREATE TRIGGER inbox_ai AFTER INSERT ON inbox BEGIN
              INSERT INTO docs(id, title, body) VALUES (NEW.id, 'from trigger', NEW.text);
            END;
            """);

        Execute(connection, "INSERT INTO inbox VALUES (1, 'trigger body text');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'trigger');")
            .Should().Equal(1);

        Execute(connection, "DELETE FROM docs WHERE id = 1;");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'trigger');")
            .Should().BeEmpty();
    }

    [Test]
    public void ForeignKeyCascadesMaintainTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE owners(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT, owner INTEGER REFERENCES owners(id) ON DELETE CASCADE);");
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO owners VALUES (1);");
        Execute(connection, "INSERT INTO docs VALUES (1, 'cascade', 'cascade body', 1);");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'cascade');").Should().Equal(1);

        Execute(connection, "DELETE FROM owners WHERE id = 1;");

        QueryIntegers(connection, "SELECT count(*) FROM docs;").Should().Equal(0);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'cascade');").Should().BeEmpty();
    }

    [Test]
    public void ForeignKeySetNullMaintainsTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE owners(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT REFERENCES owners(id) ON DELETE SET NULL);");
        Execute(connection, CreateFtsIndex);
        Execute(connection, "INSERT INTO owners VALUES (1);");
        Execute(connection, "INSERT INTO docs VALUES (1, 'kept title', 1);");

        Execute(connection, "DELETE FROM owners WHERE id = 1;");

        // The cascade rewrote body to NULL; the derived state has to follow the new image.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'kept');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, '1');").Should().BeEmpty();
    }

    [Test]
    public void DroppingTheIndexInsideARolledBackTransactionRestoresIt()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "BEGIN;");
        Execute(connection, "DROP INDEX docs_fts;");
        Execute(connection, "ROLLBACK;");

        // The index is back and still answers correctly, which means Destroy did not leak into the
        // restored catalog snapshot.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3);
    }

    [Test]
    public void CreatingTheIndexInsideARolledBackTransactionLeavesNoState()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "INSERT INTO docs VALUES (1, 'alpha', 'alpha body');");

        Execute(connection, "BEGIN;");
        Execute(connection, CreateFtsIndex);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'alpha');").Should().Equal(1);
        Execute(connection, "ROLLBACK;");

        // The index no longer exists, so the scalar path answers, and nothing references the
        // discarded attachment.
        ShouldThrow(connection, "DROP INDEX docs_fts;").Message.Should().Contain("no such index");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'alpha');").Should().Equal(1);
    }

    [Test]
    public void MethodStateSurvivesACommitAndAReopen()
    {
        var path = CreateDatabasePath("managed-index-method-transactions");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
                Execute(connection, "BEGIN;");
                Execute(connection, "INSERT INTO docs VALUES (5, 'persisted', 'persisted body');");
                Execute(connection, "COMMIT;");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'persisted');")
                    .Should().Equal(5);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ARolledBackTransactionIsNotPersisted()
    {
        var path = CreateDatabasePath("managed-index-method-transactions");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                SeedCorpus(connection);
                Execute(connection, "BEGIN;");
                Execute(connection, "INSERT INTO docs VALUES (5, 'ghost', 'ghost body');");
                Execute(connection, "ROLLBACK;");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'ghost');")
                    .Should().BeEmpty();
                QueryIntegers(connection, "SELECT count(*) FROM docs;").Should().Equal(4);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void BulkDmlInsideOneTransactionStaysConsistent()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);

        Execute(connection, "BEGIN;");
        for (var id = 1; id <= 50; id++)
            Execute(connection, $"INSERT INTO docs VALUES ({id}, 'title{id}', 'body word{id % 5}');");

        for (var id = 1; id <= 25; id++)
            Execute(connection, $"UPDATE docs SET body = 'rewritten word{id % 3}' WHERE id = {id};");

        Execute(connection, "DELETE FROM docs WHERE id % 10 = 0;");
        Execute(connection, "COMMIT;");

        var indexed = QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'rewritten') ORDER BY id;");
        Execute(connection, "DROP INDEX docs_fts;");
        var scanned = QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'rewritten') ORDER BY id;");

        indexed.Should().NotBeEmpty();
        indexed.Should().Equal(scanned);
    }
}
