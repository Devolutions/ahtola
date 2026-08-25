using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Snapshot, MVCC and connection-lifetime behavior for method indexes. All method state is derived
/// from the base rows the engine already snapshots per connection and per transaction, so these
/// tests prove the derived state follows the same isolation rules rather than caching across it.
/// </summary>
[NonParallelizable]
public sealed class ManagedIndexMethodConcurrencyTests
{
    [Test]
    public void MvccJournalModeKeepsMethodIndexQueriesCorrect()
    {
        var path = CreateDatabasePath("managed-index-method-concurrency");
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();
            Execute(connection, "PRAGMA journal_mode=mvcc;");
            SeedCorpus(connection);

            database.IsMvccEnabled.Should().BeTrue();

            // fts declares TransactionalBackingStore, so reads and writes are both allowed and the
            // answer must equal the ordinary path's answer.
            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);

            Execute(connection, "INSERT INTO docs(id, title, body) VALUES (5, 'mvcc', 'mvcc mongoose');");
            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'mongoose');")
                .Should().Equal(5);

            Execute(connection, "BEGIN;");
            Execute(connection, "DELETE FROM docs WHERE id = 5;");
            Execute(connection, "ROLLBACK;");
            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'mongoose');")
                .Should().Equal(5);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void EachConnectionSeesItsOwnCommittedSnapshot()
    {
        var path = CreateDatabasePath("managed-index-method-concurrency");
        try
        {
            using (var seed = EmbeddedDatabase.OpenFile(path))
            using (var seedConnection = seed.Connect())
            {
                SeedCorpus(seedConnection);
            }

            using var database = EmbeddedDatabase.OpenFile(path);
            using var writer = database.Connect();
            using var reader = database.Connect();

            QueryIntegers(reader, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);

            Execute(writer, "INSERT INTO docs(id, title, body) VALUES (6, 'shared', 'shared skunk');");

            QueryIntegers(reader, "SELECT id FROM docs WHERE fts_match(title, body, 'skunk');")
                .Should().Equal(6);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void RepeatedStatementsDoNotLeakCursorsOrDriftResults()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        for (var iteration = 0; iteration < 200; iteration++)
        {
            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
                .Should().Equal(1, 3);
        }

        Execute(connection, "INSERT INTO docs(id, title, body) VALUES (7, 'later', 'later fox');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3, 7);
    }

    [Test]
    public void BulkDmlKeepsTheIndexConsistentUnderPressure()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX t_fts ON t USING fts (body);");

        Execute(connection, "BEGIN;");
        for (var id = 1; id <= 500; id++)
            Execute(connection, $"INSERT INTO t VALUES ({id}, 'doc{id % 7} shared token');");
        Execute(connection, "COMMIT;");

        QueryIntegers(connection, "SELECT count(*) FROM t WHERE fts_match(body, 'shared');").Should().Equal(500);
        QueryIntegers(connection, "SELECT count(*) FROM t WHERE fts_match(body, 'doc3');").Should().Equal(72);

        Execute(connection, "DELETE FROM t WHERE id % 2 = 0;");
        QueryIntegers(connection, "SELECT count(*) FROM t WHERE fts_match(body, 'shared');").Should().Equal(250);

        Execute(connection, "UPDATE t SET body = 'rewritten token' WHERE id <= 51;");
        QueryIntegers(connection, "SELECT count(*) FROM t WHERE fts_match(body, 'rewritten');").Should().Equal(26);
        QueryIntegers(connection, "SELECT count(*) FROM t WHERE fts_match(body, 'shared');").Should().Equal(224);
    }

    [Test]
    public void ConcurrentReadersOnSeparateConnectionsAgree()
    {
        using var database = new EmbeddedDatabase();
        using var seedConnection = database.Connect();
        SeedCorpus(seedConnection);

        var results = new IReadOnlyList<long>[4];
        var threads = new Thread[results.Length];
        for (var index = 0; index < threads.Length; index++)
        {
            var slot = index;
            threads[slot] = new Thread(() =>
            {
                using var connection = database.Connect();
                results[slot] = QueryIntegers(
                    connection,
                    "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;");
            });
            threads[slot].Start();
        }

        foreach (var thread in threads)
            thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

        foreach (var result in results)
            result.Should().Equal(1, 3);
    }
}
