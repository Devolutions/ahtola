using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>Grammar, round-trip and rejection matrix for <c>CREATE INDEX … USING … WITH (…)</c>.</summary>
public sealed class ManagedIndexMethodSyntaxTests
{
    [Test]
    public void MethodIndexRoundTripsThroughSqliteSchema()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(
            connection,
            "CREATE INDEX docs_fts ON docs USING fts (title, body) WITH (tokenizer = 'unicode61', weights = 'title=2.0');");

        var sql = QueryTexts(connection, "SELECT sql FROM sqlite_master WHERE name = 'docs_fts';").Single();
        sql.Should().Contain("USING fts")
            .And.Contain("(title, body)")
            .And.Contain("WITH (tokenizer = 'unicode61', weights = 'title=2.0')");
    }

    [Test]
    public void MethodIndexWithNoParametersOmitsTheWithClause()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, CreateFtsIndex);

        QueryTexts(connection, "SELECT sql FROM sqlite_master WHERE name = 'docs_fts';")
            .Single().Should().NotContain(" WITH (");
    }

    [Test]
    public void UnknownMethodIsRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX docs_x ON docs USING nosuch (body);")
            .Message.Should().Be("no such index method: nosuch");
    }

    [Test]
    public void UnknownWithKeyIsRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX docs_x ON docs USING fts (body) WITH (bogus = 1);")
            .Message.Should().Be("unknown fts index parameter: bogus");
    }

    [TestCase(
        "CREATE UNIQUE INDEX docs_x ON docs USING fts (body);",
        "UNIQUE is not supported with an index method")]
    [TestCase(
        "CREATE INDEX docs_x ON docs USING fts (body) WHERE body IS NOT NULL;",
        "A partial WHERE clause is not supported with an index method")]
    [TestCase(
        "CREATE INDEX docs_x ON docs USING fts (lower(body));",
        "An index method column must be a plain column name")]
    [TestCase(
        "CREATE INDEX docs_x ON docs USING fts (body DESC);",
        "DESC is not supported on an index method column")]
    [TestCase(
        "CREATE INDEX docs_x ON docs USING fts (body COLLATE NOCASE);",
        "COLLATE is not supported on an index method column")]
    [TestCase(
        "CREATE INDEX docs_x ON docs (body) WITH (tokenizer = 'ascii');",
        "WITH is valid only on an index that declares USING")]
    public void UnsupportedShapesAreRejected(string sql, string message)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, sql).Message.Should().Contain(message);
    }

    [Test]
    public void MethodIndexOnWithoutRowidTableIsRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE wr(k TEXT PRIMARY KEY, body TEXT) WITHOUT ROWID;");

        ShouldThrow(connection, "CREATE INDEX wr_fts ON wr USING fts (body);")
            .Message.Should().Contain("WITHOUT ROWID");
    }

    [Test]
    public void MethodIndexOnAViewIsRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE VIEW docs_view AS SELECT * FROM docs;");

        ShouldThrow(connection, "CREATE INDEX v_fts ON docs_view USING fts (body);")
            .Message.Should().Contain("views may not be indexed");
    }

    [Test]
    public void MethodIndexOnSchemaTablesIsRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX s_fts ON sqlite_master USING fts (sql);")
            .Message.Should().Contain("may not be indexed");
    }

    [Test]
    public void ReservedIndexMethodNamesCannotBeCreatedByUserDdl()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX \"docs_ahtola_idxm_post\" ON docs USING fts (body);")
            .Message.Should().Contain("object name reserved for internal use");
        ShouldThrow(connection, "CREATE TABLE \"x_ahtola_idxm_meta\"(a);")
            .Message.Should().Contain("object name reserved for internal use");
    }

    [Test]
    public void ParameterLiteralsAcceptTextIntegerRealAndNegativeValues()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT);");
        Execute(
            connection,
            "CREATE INDEX t_fts ON t USING fts (body) WITH (tokenizer = 'ngram', min_gram = 2, max_gram = 3, columnsize = 1);");

        QueryTexts(connection, "SELECT sql FROM sqlite_master WHERE name = 't_fts';")
            .Single().Should().Contain("min_gram = 2").And.Contain("max_gram = 3");
    }

    [Test]
    public void ParameterExpressionsAreRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX docs_x ON docs USING fts (body) WITH (min_gram = 1 + 1);")
            .Message.Should().Contain("Expected RightParen");
        ShouldThrow(connection, "CREATE INDEX docs_x ON docs USING fts (body) WITH (tokenizer = lower('X'));")
            .Message.Should().Contain("requires a literal value");
    }

    [Test]
    public void DuplicateParameterKeysAreRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX docs_x ON docs USING fts (body) WITH (tokenizer = 'ascii', tokenizer = 'raw');")
            .Message.Should().Contain("Duplicate index method parameter");
    }

    [Test]
    public void WeightsMustNameIndexedColumns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);

        ShouldThrow(connection, "CREATE INDEX docs_x ON docs USING fts (body) WITH (weights = 'title=2.0');")
            .Message.Should().Be("no such fts column: title");
    }

    [Test]
    public void OrdinaryIndexesAreUnaffected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, CreateDocuments);
        Execute(connection, "CREATE UNIQUE INDEX docs_title ON docs(title COLLATE NOCASE DESC) WHERE title IS NOT NULL;");

        QueryTexts(connection, "SELECT sql FROM sqlite_master WHERE name = 'docs_title';")
            .Single().Should().Contain("UNIQUE").And.NotContain("USING");
    }

    [Test]
    public void DropIndexRemovesTheMethodIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        SeedCorpus(connection);

        Execute(connection, "DROP INDEX docs_fts;");
        QueryTexts(connection, "SELECT name FROM sqlite_master WHERE name = 'docs_fts';").Should().BeEmpty();

        // The scalar surface keeps working without an index; only the access path is gone.
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'fox') ORDER BY id;")
            .Should().Equal(1, 3);
    }
}
