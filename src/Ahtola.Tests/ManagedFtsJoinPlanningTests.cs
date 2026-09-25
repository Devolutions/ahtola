using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// A method-owned conjunct on one join arm (<c>fts_match(a.title, a.body, ?)</c>) is pushed into that
/// arm so its FTS index serves it, as upstream's <c>collect_index_method_candidates</c> does for
/// joins. The pushed conjunct is only an access path: the full WHERE still runs on the join output.
/// </summary>
[NonParallelizable]
public sealed class ManagedFtsJoinPlanningTests
{
    private const string Schema =
        "CREATE TABLE authors(id INTEGER PRIMARY KEY, name TEXT);" +
        "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT, author_id INTEGER);" +
        "CREATE TABLE tags(article_id INTEGER, tag TEXT);";

    [Test]
    public void FtsArmOfAnInnerJoinUsesItsMethodIndexInEitherOrder()
    {
        using var indexed = CreateCorpus(withIndex: true, out var indexedConnection);
        using var plain = CreateCorpus(withIndex: false, out var plainConnection);
        using (indexedConnection)
        using (plainConnection)
        {
            const string forward =
                "SELECT a.id, u.name FROM articles a JOIN authors u ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database') ORDER BY a.id";
            const string reversed =
                "SELECT a.id, u.name FROM authors u JOIN articles a ON u.id = a.author_id WHERE fts_match(a.title, a.body, 'database') ORDER BY a.id";

            foreach (var sql in new[] { forward, reversed })
            {
                var before = EmbeddedDatabase.MethodIndexScansExecuted;
                var rows = Rows(indexedConnection, sql);
                EmbeddedDatabase.MethodIndexScansExecuted.Should().BeGreaterThan(before, sql);
                rows.Should().Equal(Rows(plainConnection, sql), sql);
                rows.Should().HaveCount(10);
            }

            Plan(indexedConnection, forward).Should().Equal(
                Plan(indexedConnection, forward)[0],
                "SCAN authors AS u",
                "USE SORTER FOR ORDER BY");
            Plan(indexedConnection, forward)[0].Should().StartWith("SEARCH articles AS a USING INDEX METHOD fts INDEX articles_fts (pattern=Match");
            Plan(indexedConnection, reversed)[0].Should().Be("SCAN authors AS u");
            Plan(indexedConnection, reversed)[1].Should().StartWith("SEARCH articles AS a USING INDEX METHOD fts INDEX articles_fts");
        }
    }

    [Test]
    public void LeftJoinPushesOnlyIntoThePreservedArm()
    {
        using var indexed = CreateCorpus(withIndex: true, out var indexedConnection);
        using var plain = CreateCorpus(withIndex: false, out var plainConnection);
        using (indexedConnection)
        using (plainConnection)
        {
            // Preserved (left) arm: pushed, and the unmatched tags stay NULL-padded.
            const string preserved =
                "SELECT a.id, t.tag FROM articles a LEFT JOIN tags t ON t.article_id = a.id WHERE fts_match(a.title, a.body, 'database') ORDER BY a.id, t.tag";
            var before = EmbeddedDatabase.MethodIndexScansExecuted;
            var rows = Rows(indexedConnection, preserved);
            EmbeddedDatabase.MethodIndexScansExecuted.Should().BeGreaterThan(before);
            rows.Should().Equal(Rows(plainConnection, preserved));
            rows.Should().Contain(static row => row.EndsWith("|NULL", StringComparison.Ordinal));

            // Null-supplying (right) arm: never pushed; the WHERE on the padded rows decides.
            const string nullSupplying =
                "SELECT u.id, a.id FROM authors u LEFT JOIN articles a ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database') OR a.id IS NULL ORDER BY u.id, a.id";
            Rows(indexedConnection, nullSupplying).Should().Equal(Rows(plainConnection, nullSupplying));
        }
    }

    [Test]
    public void ThreeWayJoinAndCrossArmPredicatesStayCorrect()
    {
        using var indexed = CreateCorpus(withIndex: true, out var indexedConnection);
        using var plain = CreateCorpus(withIndex: false, out var plainConnection);
        using (indexedConnection)
        using (plainConnection)
        {
            var queries = new[]
            {
                "SELECT a.id, u.name, t.tag FROM articles a JOIN authors u ON a.author_id = u.id JOIN tags t ON t.article_id = a.id WHERE fts_match(a.title, a.body, 'systems') ORDER BY a.id, t.tag",
                "SELECT a.id, u.name FROM articles a JOIN authors u ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database') AND u.name <> 'Author2' ORDER BY a.id",
                // Touches both arms: stays on the join output.
                "SELECT a.id FROM articles a JOIN authors u ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database') OR u.name = 'Author3' ORDER BY a.id",
                "SELECT count(*) FROM articles a JOIN authors u ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database') AND fts_match(a.title, a.body, 'sql')",
            };

            foreach (var sql in queries)
                Rows(indexedConnection, sql).Should().Equal(Rows(plainConnection, sql), sql);

            Plan(indexedConnection, queries[0])[0].Should().StartWith("SEARCH articles AS a USING INDEX METHOD fts");
        }
    }

    [Test]
    public void ShadowedFunctionIsNeverPushedIntoAJoinArm()
    {
        using var database = CreateCorpus(withIndex: true, out var connection);
        using (connection)
        {
            connection.RegisterScalarFunction("fts_match", -1, static _ => SqlValue.Integer(1));
            const string sql =
                "SELECT count(*) FROM articles a JOIN authors u ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database')";
            var before = EmbeddedDatabase.MethodIndexScansExecuted;
            QueryIntegers(connection, sql).Should().Equal(100);
            EmbeddedDatabase.MethodIndexScansExecuted.Should().Be(before);
            string.Join(" | ", Plan(connection, sql)).Should().NotContain("INDEX METHOD");
        }
    }

    private static EmbeddedDatabase CreateCorpus(bool withIndex, out EmbeddedConnection connection)
    {
        var database = new EmbeddedDatabase();
        connection = database.Connect();
        foreach (var statement in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
            Execute(connection, statement + ";");
        if (withIndex)
            Execute(connection, "CREATE INDEX articles_fts ON articles USING fts(title, body);");

        for (var id = 1; id <= 5; id++)
            Execute(connection, $"INSERT INTO authors VALUES ({id}, 'Author{id}');");

        for (var id = 1; id <= 100; id++)
        {
            var (title, body) = id % 10 == 0
                ? ($"Database Article {id}", "Content about database systems and SQL")
                : ($"General Article {id}", "General content about various topics");
            Execute(connection, $"INSERT INTO articles VALUES ({id}, '{title}', '{body}', {id % 5 + 1});");
            if (id % 20 != 0)
                Execute(connection, $"INSERT INTO tags VALUES ({id}, 'tag{id % 3}');");
        }

        return database;
    }

    private static string[] Rows(EmbeddedConnection connection, string sql)
        => Query(connection, sql)
            .Select(static row => string.Join("|", row.Select(static value => Format(value))))
            .ToArray();

    private static string Format(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "NULL",
            SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => value.AsText(),
        };

    private static string[] Plan(EmbeddedConnection connection, string sql)
        => Query(connection, "EXPLAIN QUERY PLAN " + sql).Select(static row => row[3].AsText()).ToArray();
}
