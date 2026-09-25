using Ahtola.Core;
using Ahtola.Core.Indexing;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// A committed write must leave the FTS index incrementally maintained: the next query applies the
/// write's delta instead of rebuilding the whole index from the base rows. Every write transaction
/// and statement backup clones the table, so this holds only because state-carrying forks share
/// the immutable segments and every whole-row-list rewrite reports its exact change set.
/// </summary>
/// <remarks>
/// A twin table without an index is the oracle: <c>fts_match</c> evaluated per row over the same
/// rows must agree with the indexed answer after every step.
/// </remarks>
[NonParallelizable]
public sealed class ManagedFtsIncrementalMaintenanceTests
{
    private static readonly string[] Writes =
    [
        "INSERT INTO {0}(id, body, tag) VALUES (1001, 'alpha fresh', 'n1')",
        "INSERT INTO {0}(id, body, tag) VALUES (1002, 'beta fresh', 'n2'), (1003, 'alpha beta', 'n3')",
        "UPDATE {0} SET body = 'gamma rewritten' WHERE id = 5",
        "UPDATE {0} SET body = body || ' alpha' WHERE id BETWEEN 10 AND 20",
        "UPDATE {0} SET id = 5000 WHERE id = 30",
        "DELETE FROM {0} WHERE id = 40",
        "DELETE FROM {0} WHERE id BETWEEN 50 AND 60",
        "INSERT OR REPLACE INTO {0}(id, body, tag) VALUES (70, 'alpha replaced', 't70')",
        "INSERT INTO {0}(id, body, tag) VALUES (80, 'beta upserted', 'x') ON CONFLICT(id) DO UPDATE SET body = excluded.body",
        "REPLACE INTO {0}(id, body, tag) VALUES (2000, 'gamma via tag', 't90')",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public void EveryCommittedWriteShapeIsAppliedIncrementally(bool fileBacked)
    {
        var path = fileBacked ? CreateDatabasePath("fts-incremental") : null;
        try
        {
            using var database = path is null ? new EmbeddedDatabase() : EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();
            Seed(connection);
            AssertAgrees(connection);

            foreach (var write in Writes)
            {
                var before = ManagedIndexMethodDiagnostics.StateRebuilds;
                ApplyToBoth(connection, write);
                AssertAgrees(connection);
                ManagedIndexMethodDiagnostics.StateRebuilds.Should().Be(before, write);
            }

            // The same shapes inside one explicit transaction, queried before and after COMMIT.
            var beforeTransaction = ManagedIndexMethodDiagnostics.StateRebuilds;
            Execute(connection, "BEGIN");
            ApplyToBoth(connection, "UPDATE {0} SET body = 'delta inside' WHERE id = 7");
            ApplyToBoth(connection, "DELETE FROM {0} WHERE id = 8");
            AssertAgrees(connection);
            Execute(connection, "COMMIT");
            AssertAgrees(connection);
            ManagedIndexMethodDiagnostics.StateRebuilds.Should().Be(beforeTransaction);
        }
        finally
        {
            if (path is not null)
                DeleteDatabase(path);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RolledBackWritesNeverSurviveInTheIndex(bool fileBacked)
    {
        var path = fileBacked ? CreateDatabasePath("fts-rollback") : null;
        try
        {
            using var database = path is null ? new EmbeddedDatabase() : EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();
            Seed(connection);
            AssertAgrees(connection);

            Execute(connection, "BEGIN");
            ApplyToBoth(connection, "INSERT INTO {0}(id, body, tag) VALUES (3000, 'phantom alpha', 'p')");
            ApplyToBoth(connection, "UPDATE {0} SET body = 'phantom beta' WHERE id = 3");
            ApplyToBoth(connection, "DELETE FROM {0} WHERE id = 4");
            AssertAgrees(connection);
            Execute(connection, "ROLLBACK");
            AssertAgrees(connection);
            QueryIntegers(connection, "SELECT count(*) FROM docs WHERE fts_match(body, 'phantom')").Should().Equal(0);

            Execute(connection, "BEGIN");
            ApplyToBoth(connection, "INSERT INTO {0}(id, body, tag) VALUES (3001, 'kept alpha', 'k')");
            Execute(connection, "SAVEPOINT inner_step");
            ApplyToBoth(connection, "UPDATE {0} SET body = 'phantom gamma' WHERE id = 6");
            AssertAgrees(connection);
            Execute(connection, "ROLLBACK TO inner_step");
            Execute(connection, "RELEASE inner_step");
            Execute(connection, "COMMIT");
            AssertAgrees(connection);
            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'kept')").Should().Equal(3001);

            // A statement that fails part-way (the tag column is UNIQUE) leaves nothing behind.
            ShouldThrow(connection, "UPDATE docs SET body = 'phantom delta', tag = 'dup' WHERE id IN (9, 11)");
            AssertAgrees(connection);
            QueryIntegers(connection, "SELECT count(*) FROM docs WHERE fts_match(body, 'phantom')").Should().Equal(0);
        }
        finally
        {
            if (path is not null)
                DeleteDatabase(path);
        }
    }

    private static void Seed(EmbeddedConnection connection)
    {
        foreach (var table in new[] { "docs", "plain" })
            Execute(connection, $"CREATE TABLE {table}(id INTEGER PRIMARY KEY, body TEXT, tag TEXT UNIQUE)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body)");

        var words = new[] { "alpha", "beta", "gamma", "delta", "epsilon" };
        Execute(connection, "BEGIN");
        for (var id = 1; id <= 400; id++)
        {
            var body = $"{words[id % 5]} {words[(id * 7) % 5]} filler{id % 13}";
            ApplyToBoth(connection, $"INSERT INTO {{0}}(id, body, tag) VALUES ({id}, '{body}', 't{id}')");
        }

        Execute(connection, "COMMIT");
    }

    private static void ApplyToBoth(EmbeddedConnection connection, string template)
    {
        Execute(connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "docs"));
        Execute(connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "plain"));
    }

    private static void AssertAgrees(EmbeddedConnection connection)
    {
        foreach (var query in new[] { "alpha", "beta AND gamma", "fresh", "phantom", "delta NOT alpha", "filler1*" })
        {
            var indexed = QueryIntegers(connection, $"SELECT id FROM docs WHERE fts_match(body, '{query}') ORDER BY id");
            var scanned = QueryIntegers(connection, $"SELECT id FROM plain WHERE fts_match(body, '{query}') ORDER BY id");
            indexed.Should().Equal(scanned, query);
        }

        QueryIntegers(connection, "SELECT count(*) FROM docs").Should().Equal(QueryIntegers(connection, "SELECT count(*) FROM plain"));
    }
}
