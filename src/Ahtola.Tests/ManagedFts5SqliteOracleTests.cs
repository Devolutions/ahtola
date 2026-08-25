using Ahtola.Core;
using AwesomeAssertions;
using SQLitePCL;
using MsData = Microsoft.Data.Sqlite;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>Review regressions measured directly against stock SQLite's FTS5 implementation.</summary>
public sealed class ManagedFts5SqliteOracleTests
{
    [OneTimeSetUp]
    public void InitializeSqlite() => Batteries_V2.Init();

    [Test]
    public void CorrelatedAuxiliaryArgumentsBindToTheInnermostResolvingScope()
    {
        const string setup =
            """
            CREATE VIRTUAL TABLE ft USING fts5(body);
            INSERT INTO ft(rowid, body) VALUES (1, 'one two');
            CREATE TABLE local_shadow(ft TEXT);
            INSERT INTO local_shadow VALUES ('ordinary');
            CREATE TABLE no_shadow(x TEXT);
            INSERT INTO no_shadow VALUES ('ordinary');
            """;

        foreach (var auxiliary in new[]
                 {
                     "highlight(ft, 0, '[', ']')",
                     "snippet(ft, 0, '[', ']', '...', 5)",
                     "bm25(ft)",
                 })
        {
            var query = $"SELECT (SELECT {auxiliary} FROM local_shadow) FROM ft WHERE ft MATCH 'one';";
            AssertBothFail(setup, query, "unable to use function");
        }

        AssertTextRowsMatch(
            setup,
            "SELECT (SELECT highlight(ft, 0, '[', ']') FROM no_shadow) FROM ft WHERE ft MATCH 'one';",
            "[one] two");
    }

    [Test]
    public void ColumnMatchIntersectsItsImplicitColumnWithExplicitQueryFilters()
    {
        const string setup =
            """
            CREATE VIRTUAL TABLE ft USING fts5(title, body);
            INSERT INTO ft(rowid, title, body) VALUES
              (1, 'one', 'two'),
              (2, 'two', 'one'),
              (3, 'one two', 'one two');
            """;

        AssertRowIdsMatch(setup, "SELECT rowid FROM ft WHERE title MATCH 'body:one' ORDER BY rowid;");
        AssertRowIdsMatch(
            setup,
            "SELECT rowid FROM ft WHERE title MATCH 'title:one' ORDER BY rowid;",
            1,
            3);
        AssertRowIdsMatch(
            setup,
            "SELECT rowid FROM ft WHERE title MATCH 'body:(one OR two)' ORDER BY rowid;");
    }

    [Test]
    public void BlobContentIsDecodedAsUtf8ForIndexingAndHighlighting()
    {
        const string setup =
            """
            CREATE VIRTUAL TABLE ft USING fts5(body);
            INSERT INTO ft(rowid, body) VALUES (1, X'636166C3A9206F6E65');
            """;

        AssertRowIdsMatch(setup, "SELECT rowid FROM ft WHERE ft MATCH 'cafe';", 1);
        AssertRowIdsMatch(setup, "SELECT rowid FROM ft WHERE ft MATCH 'one';", 1);
        AssertTextRowsMatch(
            setup,
            "SELECT hex(highlight(ft, 0, '[', ']')) FROM ft WHERE ft MATCH 'cafe';",
            "5B636166C3A95D206F6E65");
        AssertTextRowsMatch(
            setup,
            "SELECT hex(highlight(ft, 0, '[', ']')) FROM ft WHERE ft MATCH 'one';",
            "636166C3A9205B6F6E655D");
    }

    [Test]
    public void BinaryNotBindsMoreLooselyThanImplicitAnd()
    {
        const string setup =
            """
            CREATE VIRTUAL TABLE ft USING fts5(body);
            INSERT INTO ft(rowid, body) VALUES
              (1, 'one'),
              (2, 'one two'),
              (3, 'one three'),
              (4, 'one two three'),
              (5, 'two three'),
              (6, 'one two x three'),
              (7, 'one two thrice');
            """;

        AssertRowIdsMatch(
            setup,
            "SELECT rowid FROM ft WHERE ft MATCH 'one NOT two three' ORDER BY rowid;",
            1,
            2,
            3,
            7);
    }

    [Test]
    public void Fts5UsesModernNearAndQuotedPhrasePrefixGrammar()
    {
        const string setup =
            """
            CREATE VIRTUAL TABLE ft USING fts5(body);
            INSERT INTO ft(rowid, body) VALUES
              (1, 'one'),
              (2, 'one two'),
              (3, 'one three'),
              (4, 'one two three'),
              (5, 'two three'),
              (6, 'one two x three'),
              (7, 'one two thrice');
            """;

        AssertBothFail(
            setup,
            "SELECT rowid FROM ft WHERE ft MATCH 'NEAR/1(one two)';",
            "syntax");
        AssertRowIdsMatch(
            setup,
            "SELECT rowid FROM ft WHERE ft MATCH 'NEAR(one two, 1)' ORDER BY rowid;",
            2,
            4,
            6,
            7);
        AssertRowIdsMatch(
            setup,
            """SELECT rowid FROM ft WHERE ft MATCH '"one two thr"*' ORDER BY rowid;""",
            4,
            7);
    }

    private static void AssertRowIdsMatch(string setup, string query, params long[] expected)
    {
        using var managed = OpenManaged(setup);
        using var sqlite = OpenSqlite(setup);

        var managedRows = QueryIntegers(managed, query);
        var sqliteRows = QuerySqliteInt64(sqlite, query);
        managedRows.Should().Equal(sqliteRows);
        managedRows.Should().Equal(expected);
    }

    private static void AssertTextRowsMatch(string setup, string query, params string[] expected)
    {
        using var managed = OpenManaged(setup);
        using var sqlite = OpenSqlite(setup);

        var managedRows = QueryTexts(managed, query);
        var sqliteRows = QuerySqliteText(sqlite, query);
        managedRows.Should().Equal(sqliteRows);
        managedRows.Should().Equal(expected);
    }

    private static void AssertBothFail(string setup, string query, string messageFragment)
    {
        using var managed = OpenManaged(setup);
        using var sqlite = OpenSqlite(setup);

        var managedAction = () => Execute(managed, query);
        managedAction.Should().Throw<EmbeddedSqlException>().WithMessage($"*{messageFragment}*");

        var sqliteAction = () =>
        {
            using var command = sqlite.CreateCommand();
            command.CommandText = query;
            command.ExecuteNonQuery();
        };
        sqliteAction.Should().Throw<MsData.SqliteException>().WithMessage($"*{messageFragment}*");
    }

    private static EmbeddedConnection OpenManaged(string setup)
    {
        var connection = new EmbeddedDatabase().Connect();
        foreach (var statement in setup.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Execute(connection, statement + ";");
        return connection;
    }

    private static MsData.SqliteConnection OpenSqlite(string setup)
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = setup;
        command.ExecuteNonQuery();
        return connection;
    }

    private static IReadOnlyList<long> QuerySqliteInt64(MsData.SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var rows = new List<long>();
        while (reader.Read())
            rows.Add(reader.GetInt64(0));
        return rows;
    }

    private static IReadOnlyList<string> QuerySqliteText(MsData.SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
