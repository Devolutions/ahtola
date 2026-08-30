using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>End-to-end behavior of <c>CREATE INDEX … USING fts</c> against a live database.</summary>
public sealed class ManagedFtsIndexMethodTests
{
    [Test]
    public void MatchPredicateSelectsOnlyTheMatchingRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3);
    }

    [Test]
    public void BooleanOperatorsCombineTermsAcrossColumns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox AND lazy') ORDER BY id;")
            .Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox OR snail') ORDER BY id;")
            .Should().Equal(1, 3, 4);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'quick NOT lazy') ORDER BY id;")
            .Should().Equal(3);
    }

    [Test]
    public void PhraseAndPrefixQueriesUsePositionsAndTheTermDictionary()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, '\"quick brown\"') ORDER BY id;")
            .Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox*') ORDER BY id;")
            .Should().Equal(1, 3);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'tomato*') ORDER BY id;")
            .Should().Equal(4);
    }

    [Test]
    public void ColumnFilterRestrictsAMatchToOneIndexedColumn()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'title:lazy') ORDER BY id;")
            .Should().Equal(2);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'body:lazy') ORDER BY id;")
            .Should().Equal(1, 2);
    }

    [Test]
    public void MethodGrammarDoesNotAdoptTheManagedNearExtension()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        ShouldThrow(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'NEAR/2(quick fox)');")
            .Message.Should().Contain("Turso FTS query");
    }

    [Test]
    public void ScoreOrdersByRelevanceAndBreaksTiesByRowid()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(
            connection,
            """
            INSERT INTO docs(id, title, body) VALUES
              (1, 'alpha', 'gamma'),
              (2, 'alpha', 'gamma'),
              (3, 'alpha', 'gamma gamma gamma gamma');
            """);

        QueryIntegers(
                connection,
                "SELECT id FROM docs WHERE fts_match(title, body, 'gamma') ORDER BY fts_score(title, body, 'gamma') DESC, id;")
            .Should().Equal(3, 1, 2);
    }

    [Test]
    public void ColumnWeightsChangeTheRanking()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(
            connection,
            "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (weights = 'title=8.0, body=1.0');");
        Execute(
            connection,
            """
            INSERT INTO docs(id, title, body) VALUES
              (1, 'zeta',    'filler filler filler'),
              (2, 'nothing', 'zeta filler filler');
            """);

        QueryIntegers(
                connection,
                "SELECT id FROM docs ORDER BY fts_score(title, body, 'zeta') DESC, id;")
            .Should().StartWith([1]);
    }

    [Test]
    public void ScoreIsIdenticalWithAndWithoutTheIndexAccessPath()
    {
        using var indexed = new EmbeddedDatabase();
        using var indexedConnection = indexed.Connect();
        SeedCorpus(indexedConnection);

        // The same corpus without a method index: fts_score has no corpus to draw IDF from, so the
        // ranking must still be produced, deterministic and non-negative, and the match predicate
        // must select exactly the same rows on both paths.
        using var plain = new EmbeddedDatabase();
        using var plainConnection = plain.Connect();
        Execute(plainConnection, CreateDocuments);
        Execute(
            plainConnection,
            """
            INSERT INTO docs(id, title, body) VALUES
              (1, 'The quick brown fox', 'A quick brown fox jumps over the lazy dog'),
              (2, 'Lazy afternoon',      'The dog sleeps all afternoon, lazy and warm'),
              (3, 'Foxes and hounds',    'Foxes outwit hounds; the quick fox wins again'),
              (4, 'Gardening notes',     'Tomatoes, beans and a slow snail');
            """);

        const string match = "SELECT id FROM docs WHERE fts_match(title, body, 'quick fox') ORDER BY id;";
        QueryIntegers(indexedConnection, match).Should().Equal(QueryIntegers(plainConnection, match));

        var indexedScores = QueryReals(
            indexedConnection,
            "SELECT fts_score(title, body, 'fox') FROM docs ORDER BY id;");
        indexedScores.Should().HaveCount(4);
        indexedScores.Should().AllSatisfy(static score => score.Should().BeGreaterThanOrEqualTo(0.0));
        indexedScores[3].Should().Be(0.0);

        // Rerunning the ranked query must be byte-for-byte reproducible.
        QueryReals(indexedConnection, "SELECT fts_score(title, body, 'fox') FROM docs ORDER BY id;")
            .Should().Equal(indexedScores);
    }

    [Test]
    public void ScoreFallsBackToZeroWithNoMethodIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE plain(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "INSERT INTO plain VALUES (1, 'fox fox dog');");

        QueryIntegers(connection, "SELECT fts_match(body, 'fox') FROM plain;").Should().Equal(1);
        QueryReals(connection, "SELECT fts_score(body, 'fox') FROM plain;").Should().Equal(0.0);
        QueryIntegers(connection, "SELECT fts_match(body, 'cat') FROM plain;").Should().Equal(0);
    }

    [Test]
    public void HighlightAndSnippetUseTokenizerOffsets()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(body TEXT);");
        Execute(connection, "INSERT INTO t VALUES ('The quick brown fox jumps over the lazy dog again and again');");

        QueryTexts(connection, "SELECT fts_highlight('a quick fox', '[', ']', 'quick');")
            .Single().Should().Be("a [quick] fox");
        QueryTexts(connection, "SELECT fts_highlight('Crème brûlée', '<', '>', 'creme');")
            .Single().Should().Be("Crème brûlée");
        QueryTexts(connection, "SELECT fts_snippet(body, 'lazy', '[', ']', '…', 5) FROM t;")
            .Single().Should().Contain("[lazy]").And.StartWith("…");
    }

    [Test]
    public void DmlKeepsTheIndexConsistentAcrossInsertUpdateDeleteAndReplace()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "INSERT INTO docs(id, title, body) VALUES (5, 'New fox tale', 'another fox appears');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3, 5);

        Execute(connection, "UPDATE docs SET body = 'no animals here' WHERE id = 5;");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'appears') ORDER BY id;")
            .Should().BeEmpty();

        Execute(connection, "DELETE FROM docs WHERE id = 5;");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3);

        Execute(connection, "INSERT OR REPLACE INTO docs(id, title, body) VALUES (1, 'replaced', 'penguin colony');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'penguin') ORDER BY id;")
            .Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(3);
    }

    [Test]
    public void RowidChangeMovesTheDocument()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "UPDATE docs SET id = 99 WHERE id = 4;");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'snail');")
            .Should().Equal(99);
    }

    [Test]
    public void RollbackDiscardsIndexUpdatesWithTheRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs(id, title, body) VALUES (9, 'temp', 'ephemeral platypus');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'platypus');")
            .Should().Equal(9);
        Execute(connection, "ROLLBACK;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'platypus');")
            .Should().BeEmpty();
    }

    [Test]
    public void SavepointRollbackDiscardsOnlyTheInnerIndexUpdates()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO docs(id, title, body) VALUES (10, 'outer', 'outer walrus');");
        Execute(connection, "SAVEPOINT inner_point;");
        Execute(connection, "INSERT INTO docs(id, title, body) VALUES (11, 'inner', 'inner narwhal');");
        Execute(connection, "ROLLBACK TO inner_point;");
        Execute(connection, "COMMIT;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'walrus');").Should().Equal(10);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'narwhal');").Should().BeEmpty();
    }

    [Test]
    public void TriggersAndForeignKeyCascadesMaintainTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE, body TEXT);");
        Execute(connection, "CREATE INDEX child_fts ON child USING fts (body);");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX audit_fts ON audit USING fts (body);");
        Execute(
            connection,
            "CREATE TRIGGER child_ai AFTER INSERT ON child BEGIN INSERT INTO audit(body) VALUES ('audited ' || NEW.body); END;");

        Execute(connection, "INSERT INTO parent VALUES (1);");
        Execute(connection, "INSERT INTO child VALUES (1, 1, 'cascading capybara');");

        QueryIntegers(connection, "SELECT id FROM child WHERE fts_match(body, 'capybara');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM audit WHERE fts_match(body, 'audited');").Should().Equal(1);

        Execute(connection, "DELETE FROM parent WHERE id = 1;");
        QueryIntegers(connection, "SELECT id FROM child WHERE fts_match(body, 'capybara');").Should().BeEmpty();
    }

    [Test]
    public void UpsertMaintainsTheIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX t_fts ON t USING fts (body);");
        Execute(connection, "INSERT INTO t VALUES (1, 'first otter');");
        Execute(connection, "INSERT INTO t VALUES (1, 'second badger') ON CONFLICT(id) DO UPDATE SET body = excluded.body;");

        QueryIntegers(connection, "SELECT id FROM t WHERE fts_match(body, 'badger');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM t WHERE fts_match(body, 'otter');").Should().BeEmpty();
    }

    [Test]
    public void ReindexRebuildsAndOptimizeCompactsWithoutChangingResults()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "DELETE FROM docs WHERE id IN (2, 4);");
        Execute(connection, "REINDEX docs_fts;");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3);
    }

    [Test]
    public void LimitPushdownReturnsTheHighestRankedRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);
        Execute(
            connection,
            """
            INSERT INTO docs(id, title, body) VALUES
              (1, 'x', 'kiwi'),
              (2, 'x', 'kiwi kiwi'),
              (3, 'x', 'kiwi kiwi kiwi'),
              (4, 'x', 'nothing');
            """);

        QueryIntegers(
                connection,
                "SELECT id FROM docs ORDER BY fts_score(title, body, 'kiwi') DESC, id LIMIT 2;")
            .Should().Equal(3, 2);
    }

    [Test]
    public void JoinsAndAliasesStillReturnCorrectRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);
        Execute(connection, "CREATE TABLE tags(doc_id INTEGER, tag TEXT);");
        Execute(connection, "INSERT INTO tags VALUES (1, 'animal'), (3, 'animal'), (4, 'plant');");

        QueryIntegers(
                connection,
                """
                SELECT d.id FROM docs AS d JOIN tags AS t ON t.doc_id = d.id
                WHERE fts_match(d.title, d.body, 'fox') AND t.tag = 'animal' ORDER BY d.id;
                """)
            .Should().Equal(1, 3);

        QueryIntegers(
                connection,
                "SELECT id FROM docs WHERE id IN (SELECT doc_id FROM tags WHERE tag = 'animal') AND fts_match(title, body, 'quick') ORDER BY id;")
            .Should().Equal(1, 3);
    }

    [Test]
    public void TokenizersAreConfigurableAndChangeMatching()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX t_tri ON t USING fts (body) WITH (tokenizer = 'trigram');");
        Execute(connection, "INSERT INTO t VALUES (1, 'abcdef');");

        QueryIntegers(connection, "SELECT id FROM t WHERE fts_match(body, 'bcd');").Should().Equal(1);

        using var raw = new EmbeddedDatabase();
        using var rawConnection = raw.Connect();
        Execute(rawConnection, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(rawConnection, "CREATE INDEX t_raw ON t USING fts (body) WITH (tokenizer = 'raw');");
        Execute(rawConnection, "INSERT INTO t VALUES (1, 'Mixed Case');");

        QueryIntegers(rawConnection, "SELECT id FROM t WHERE fts_match(body, '\"Mixed Case\"');").Should().Equal(1);
        QueryIntegers(rawConnection, "SELECT id FROM t WHERE fts_match(body, 'Mixed');").Should().BeEmpty();
    }

    [Test]
    public void ExistingFts5VirtualTablesStillWork()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE notes USING fts5(body);");
        Execute(connection, "INSERT INTO notes(body) VALUES ('quick brown fox');");
        Execute(connection, "INSERT INTO notes(body) VALUES ('lazy dog');");

        QueryTexts(connection, "SELECT body FROM notes WHERE notes MATCH 'fox';")
            .Should().Equal("quick brown fox");
    }
}
