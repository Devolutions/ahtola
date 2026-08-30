using System.Text;
using Ahtola.Core;
using Ahtola.Core.Search;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>Observable parity with Turso's pinned <c>CREATE INDEX ... USING fts</c> surface.</summary>
public sealed class ManagedFtsTursoParityTests
{
    [Test]
    public void MethodQueriesUseTantivyBooleanPrecedenceWhileFts5KeepsImplicitAnd()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body);");
        Execute(connection, "INSERT INTO docs VALUES (1,'alpha'),(2,'beta'),(3,'alpha beta'),(4,'gamma');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'alpha beta') ORDER BY id;")
            .Should().Equal(1, 2, 3);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'alpha AND beta');")
            .Should().Equal(3);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'alpha NOT beta');")
            .Should().Equal(1);

        Execute(connection, "CREATE VIRTUAL TABLE notes USING fts5(body);");
        Execute(connection, "INSERT INTO notes(rowid,body) VALUES (1,'alpha'),(2,'beta'),(3,'alpha beta');");
        QueryIntegers(connection, "SELECT rowid FROM notes WHERE notes MATCH 'alpha beta';")
            .Should().Equal(3);
    }

    [Test]
    public void ColumnPhrasePrefixAndQueryBoostsAffectMethodResults()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(title, body);");
        Execute(
            connection,
            "INSERT INTO docs VALUES (1,'database systems','filler text'),(2,'filler text','database systems'),(3,'data','base systems');");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title,body,'title:\"database systems\"');")
            .Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title,body,'body:data*') ORDER BY id;")
            .Should().Equal(2);
        QueryIntegers(
                connection,
                "SELECT id FROM docs WHERE fts_match(title,body,'title:database^2 body:database') ORDER BY fts_score(title,body,'title:database^2 body:database') DESC;")
            .Should().Equal(1, 2);
    }

    [Test]
    public void PinnedTokenizersHaveDistinctCaseSplittingAndLengthSemantics()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE d(id INTEGER PRIMARY KEY, body);");
        Execute(connection, "CREATE INDEX d_fts ON d USING fts(body) WITH(tokenizer='default');");
        Execute(
            connection,
            $"INSERT INTO d VALUES (1,'Hello-world'),(2,'{new string('x', 40)}'),(3,'{new string('y', 39)}');");
        QueryIntegers(connection, "SELECT id FROM d WHERE fts_match(body,'hello');").Should().Equal(1);
        QueryIntegers(connection, $"SELECT id FROM d WHERE fts_match(body,'{new string('x', 40)}');").Should().BeEmpty();
        QueryIntegers(connection, $"SELECT id FROM d WHERE fts_match(body,'{new string('y', 39)}');").Should().Equal(3);

        Execute(connection, "CREATE TABLE s(id INTEGER PRIMARY KEY, body);");
        Execute(connection, "CREATE INDEX s_fts ON s USING fts(body) WITH(tokenizer='simple');");
        Execute(connection, "INSERT INTO s VALUES (1,'Case-Sensitive'),(2,'case-sensitive');");
        QueryIntegers(connection, "SELECT id FROM s WHERE fts_match(body,'Case');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM s WHERE fts_match(body,'case');").Should().Equal(2);

        Execute(connection, "CREATE TABLE w(id INTEGER PRIMARY KEY, body);");
        Execute(connection, "CREATE INDEX w_fts ON w USING fts(body) WITH(tokenizer='whitespace');");
        Execute(connection, "INSERT INTO w VALUES (1,'Hello, World');");
        QueryIntegers(connection, "SELECT id FROM w WHERE fts_match(body,'Hello,');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM w WHERE fts_match(body,'hello,');").Should().BeEmpty();

        Execute(connection, "CREATE TABLE r(id INTEGER PRIMARY KEY, body);");
        Execute(connection, "CREATE INDEX r_fts ON r USING fts(body) WITH(tokenizer='raw');");
        Execute(connection, "INSERT INTO r VALUES (1,'whole field');");
        QueryIntegers(connection, "SELECT id FROM r WHERE fts_match(body,'\"whole field\"');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM r WHERE fts_match(body,'whole');").Should().BeEmpty();

        Execute(connection, "CREATE TABLE n(id INTEGER PRIMARY KEY, body);");
        Execute(connection, "CREATE INDEX n_fts ON n USING fts(body) WITH(tokenizer='ngram');");
        Execute(connection, "INSERT INTO n VALUES (1,'iPhone');");
        QueryIntegers(connection, "SELECT id FROM n WHERE fts_match(body,'PH');").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM n WHERE fts_match(body,'P');").Should().BeEmpty();
    }

    [Test]
    public void PerColumnTokenizersRoundTripAndRemainObservable()
    {
        var path = CreateDatabasePath(nameof(PerColumnTokenizersRoundTripAndRemainObservable));
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title, body);");
                Execute(
                    connection,
                    "CREATE INDEX docs_fts ON docs USING fts(title WITH tokenizer=simple, body WITH (tokenizer=raw));");
                Execute(connection, "INSERT INTO docs VALUES (1,'Case-Sensitive','whole field'),(2,'case-sensitive','whole');");

                QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title,body,'title:Case');")
                    .Should().Equal(1);
                QueryIntegers(
                        connection,
                        "SELECT id FROM docs NOT INDEXED WHERE fts_match(body,title,'title:Case');")
                    .Should().Equal(1);
                QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title,body,'body:\"whole field\"');")
                    .Should().Equal(1);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE fts_match(title,body,'title:case');")
                .Should().Equal(2);
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE fts_match(title,body,'body:whole');")
                .Should().Equal(2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void MatchOperatorRoutesColumnTupleReversedOrderAndNegation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE single(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX single_fts ON single USING fts(body);");
        Execute(connection, "INSERT INTO single VALUES (1,'alpha'),(2,'beta');");
        QueryIntegers(connection, "SELECT id FROM single WHERE body MATCH 'alpha';").Should().Equal(1);
        QueryIntegers(connection, "SELECT id FROM single WHERE body NOT MATCH 'alpha';").Should().Equal(2);

        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(title,body);");
        Execute(connection, "INSERT INTO docs VALUES (1,'alpha','none'),(2,'none','alpha');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE (body,title) MATCH 'alpha' ORDER BY id;")
            .Should().Equal(1, 2);
        QueryIntegers(connection, "SELECT id FROM docs WHERE title MATCH 'alpha';").Should().Equal(1);

        Execute(connection, "UPDATE docs SET body='updated' WHERE (title,body) MATCH 'title:alpha';");
        QueryIntegers(connection, "SELECT id FROM docs WHERE (title,body) MATCH 'body:updated';").Should().Equal(1);
        Execute(connection, "DELETE FROM docs WHERE (body,title) MATCH 'title:alpha';");
        QueryIntegers(connection, "SELECT id FROM docs ORDER BY id;").Should().Equal(2);
    }

    [Test]
    public void MatchOperatorOnANonFtsTableStillFails()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE plain(body TEXT);");
        Execute(connection, "INSERT INTO plain VALUES ('alpha');");

        ShouldThrow(connection, "SELECT * FROM plain WHERE body MATCH 'alpha';")
            .Message.Should().Contain("unable to use function MATCH");
        ShouldThrow(connection, "SELECT * FROM plain WHERE body NOT MATCH 'alpha';")
            .Message.Should().Contain("unable to use function MATCH");
    }

    [Test]
    public void TupleMatchUsesTheMethodPlanWithReversedColumns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(title,body);");
        var values = string.Join(
            ",",
            Enumerable.Range(1, 120).Select(static id =>
                $"({id},'{(id == 120 ? "needle" : "title")}','body')"));
        Execute(connection, $"INSERT INTO docs VALUES {values};");

        const string sql = "SELECT id FROM docs WHERE (body,title) MATCH 'needle';";
        Explain(connection, sql).Should().Contain("INDEX METHOD fts").And.Contain("pattern=Match");
        var before = EmbeddedDatabase.MethodIndexScansExecuted;
        QueryIntegers(connection, sql).Should().Equal(120);
        EmbeddedDatabase.MethodIndexScansExecuted.Should().Be(before + 1);
    }

    [Test]
    public void ReversedColumnCallsRetainDeclaredFieldWeights()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(
            connection,
            "CREATE INDEX docs_fts ON docs USING fts(title,body) WITH(weights='title=8,body=1');");
        Execute(connection, "INSERT INTO docs VALUES (1,'target','filler'),(2,'filler','target');");

        QueryIntegers(
                connection,
                "SELECT id FROM docs WHERE fts_match(body,title,'target') ORDER BY fts_score(body,title,'target') DESC;")
            .Should().Equal(1, 2);
    }

    [Test]
    public void ScalarNullTypeAndHighlightBehaviorMatchesPinnedTurso()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        QueryIntegers(connection, "SELECT fts_match('text',NULL);").Should().Equal(0);
        QueryReals(connection, "SELECT fts_score('text','text');").Should().Equal(0.0);
        Query(connection, "SELECT fts_score('text','text');")[0][0].Kind.Should().Be(SqlValueKind.Real);
        QueryTexts(
                connection,
                "SELECT fts_highlight(NULL,'Hello world','<b>','</b>','hello');")
            .Should().Equal("<b>Hello</b> world");
        QueryTexts(connection, "SELECT fts_highlight_legacy('Hello world','hello','<b>','</b>');")
            .Should().Equal("<b>Hello</b> world");
        Query(connection, "SELECT fts_highlight('text','<b>','</b>',NULL);")[0][0]
            .Kind.Should().Be(SqlValueKind.Null);

        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body);");
        Execute(connection, "INSERT INTO docs VALUES (1,123),(2,'123');");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body,'123') ORDER BY id;")
            .Should().Equal(2);
        SqliteBuiltinFunctions.IsDeterministic("FTS_SCORE").Should().BeTrue();
    }

    [Test]
    public void PhraseAndPrefixScoresArePositiveFloatPrecisionValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body);");
        Execute(connection, "INSERT INTO docs VALUES (1,'database systems database'),(2,'database tuning');");

        foreach (var query in new[] { "\"database systems\"", "data*" })
        {
            var scores = QueryReals(
                connection,
                $"SELECT fts_score(body,'{query.Replace("'", "''", StringComparison.Ordinal)}') FROM docs "
                + $"WHERE fts_match(body,'{query.Replace("'", "''", StringComparison.Ordinal)}') ORDER BY id;");
            scores.Should().NotBeEmpty();
            scores.Should().AllSatisfy(score =>
            {
                score.Should().BeGreaterThan(0.0);
                score.Should().Be((double)(float)score);
            });
        }
    }

    [Test]
    public void IncompatibleDuplicateIndexesDeclinePlanningWhileIdenticalOnesAreDeterministic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(connection, "CREATE INDEX a_fts ON docs USING fts(body);");
        Execute(connection, "CREATE INDEX b_fts ON docs USING fts(body) WITH(tokenizer='raw');");
        var values = string.Join(
            ",",
            Enumerable.Range(1, 120).Select(static id => $"({id},'hello world')"));
        Execute(connection, $"INSERT INTO docs VALUES {values};");

        Explain(connection, "SELECT id FROM docs WHERE fts_match(body,'hello');")
            .Should().NotContain("INDEX METHOD");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body,'hello');").Should().HaveCount(120);

        Execute(connection, "DROP INDEX b_fts;");
        Execute(connection, "CREATE INDEX b_fts ON docs USING fts(body);");
        Explain(connection, "SELECT id FROM docs WHERE fts_match(body,'hello');")
            .Should().Contain("INDEX a_fts");
    }

    [Test]
    public void PlannerExposesAllSevenPinnedFtsShapes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT);");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(title,body);");
        var values = new StringBuilder("INSERT INTO docs VALUES ");
        for (var id = 1; id <= 200; id++)
        {
            if (id > 1)
                values.Append(',');
            values.Append($"({id},'title {id}','{(id % 25 == 0 ? "needle " : string.Empty)}body')");
        }
        Execute(connection, values.Append(';').ToString());

        var cases = new (string Sql, string Shape)[]
        {
            ("SELECT id FROM docs ORDER BY fts_score(title,body,'needle') DESC LIMIT 2", "Score"),
            ("SELECT id,fts_score(title,body,'needle') FROM docs WHERE fts_match(title,body,'needle') ORDER BY fts_score(title,body,'needle') DESC LIMIT 2", "CombinedOrderedLimit"),
            ("SELECT id,fts_score(title,body,'needle') FROM docs WHERE fts_match(title,body,'needle') ORDER BY fts_score(title,body,'needle') DESC", "CombinedOrdered"),
            ("SELECT id,fts_score(title,body,'needle') FROM docs WHERE fts_match(title,body,'needle') LIMIT 2", "CombinedLimit"),
            ("SELECT id,fts_score(title,body,'needle') FROM docs WHERE fts_match(title,body,'needle')", "Combined"),
            ("SELECT id FROM docs WHERE fts_match(title,body,'needle') LIMIT 2", "MatchLimit"),
            ("SELECT id FROM docs WHERE fts_match(title,body,'needle')", "Match"),
        };

        foreach (var (sql, shape) in cases)
        {
            Explain(connection, sql).Should().Contain($"pattern={shape}", sql);
            var before = EmbeddedDatabase.MethodIndexScansExecuted;
            Query(connection, sql).Should().NotBeEmpty(sql);
            EmbeddedDatabase.MethodIndexScansExecuted.Should().Be(before + 1, sql);
        }
    }

    [Test]
    public void OrderedLimitUsesBoundedTopKWithoutATotalMatchCeiling()
    {
        var index = new ManagedFtsSearchIndex(
            1,
            ManagedFtsTokenizerOptions.Default,
            [1.0]);
        index.ColumnIndexResolver = static name => name == "body" ? 0 : null;
        for (var rowId = 1; rowId <= 20_000; rowId++)
            index.Upsert(rowId, [], [SqlValue.Text(rowId == 20_000 ? "needle needle" : "needle")]);

        var query = ManagedFtsQueryLanguage.ParseMethod(
            "needle",
            ["body"],
            [ManagedFtsTokenizerOptions.Default]);
        index.Search(query, limit: 1).Select(static hit => hit.RowId).Should().Equal(20_000);
    }

    [Test]
    public void OptimizeIndexIsTransactionalAndSurvivesReopen()
    {
        var path = CreateDatabasePath(nameof(OptimizeIndexIsTransactionalAndSurvivesReopen));
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT);");
                Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body);");
                Execute(connection, "CREATE INDEX docs_fts_2 ON docs USING fts(body);");
                Execute(connection, "INSERT INTO docs VALUES (1,'alpha'),(2,'alpha beta'),(3,'gamma');");
                Execute(connection, "DELETE FROM docs WHERE id=3;");

                Execute(connection, "BEGIN;");
                var beforeNamed = Ahtola.Core.Indexing.ManagedIndexMethodDiagnostics.StateRebuilds;
                Execute(connection, "OPTIMIZE INDEX docs_fts;");
                Ahtola.Core.Indexing.ManagedIndexMethodDiagnostics.StateRebuilds
                    .Should().BeGreaterThanOrEqualTo(beforeNamed + 1);
                Execute(connection, "ROLLBACK;");
                Execute(connection, "SAVEPOINT before_optimize;");
                Execute(connection, "OPTIMIZE INDEX;");
                Execute(connection, "ROLLBACK TO before_optimize;");
                Execute(connection, "RELEASE before_optimize;");
                var beforeAll = Ahtola.Core.Indexing.ManagedIndexMethodDiagnostics.StateRebuilds;
                Execute(connection, "OPTIMIZE INDEX;");
                Ahtola.Core.Indexing.ManagedIndexMethodDiagnostics.StateRebuilds
                    .Should().BeGreaterThanOrEqualTo(beforeAll + 2);
                QueryIntegers(connection, "SELECT id FROM docs WHERE body MATCH 'alpha' ORDER BY id;")
                    .Should().Equal(1, 2);
            }

            using var reopened = EmbeddedDatabase.OpenFile(path);
            using var reopenedConnection = reopened.Connect();
            QueryIntegers(reopenedConnection, "SELECT id FROM docs WHERE body MATCH 'alpha' ORDER BY id;")
                .Should().Equal(1, 2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static string Explain(EmbeddedConnection connection, string sql)
        => string.Join(
            "\n",
            Query(connection, "EXPLAIN QUERY PLAN " + sql)
                .Select(static row => row[3].AsText()));
}
