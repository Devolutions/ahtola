using System.Globalization;
using Ahtola.Core;
using AwesomeAssertions;
using static Ahtola.Tests.ManagedIndexMethodTestHarness;

namespace Ahtola.Tests;

/// <summary>
/// Behavioral parity oracle: a one-to-one port of Turso's pinned (<c>v0.8.0-pre.7</c>) full-text
/// search integration tests (<c>tests/integration/index_method/mod.rs</c>), the FTS model fuzz test
/// (<c>tests/fuzz/fts.rs</c>) and the SQL-observable residue of the <c>core/index_method/fts.rs</c>
/// unit tests, run against the managed <c>CREATE INDEX ... USING fts</c> implementation.
/// Assertions are kept at upstream strength so any divergence surfaces as a failing test.
/// Upstream assertions on Tantivy-internal mechanics (segment counts, retained writers, read-cache
/// counters, directory tables) have no managed equivalent and are dropped with a comment at the
/// site; the SQL-observable part of those tests is still ported.
/// </summary>
public sealed class ManagedFtsUpstreamIntegrationTests
{
    /// <summary>Port of <c>fts_mvcc_capability_is_checked_before_create</c> (mod.rs:87).</summary>
    [Test]
    public void MvccCapabilityIsCheckedBeforeCreate()
    {
        // INTENTIONAL DIVERGENCE: upstream rejects the CREATE with
        // "index method 'fts' does not support MVCC". Ahtola deliberately supports fts under MVCC
        // (declared TransactionalBackingStore, with a scan fallback), so this port asserts the CREATE
        // succeeds and fts_match answers correctly under PRAGMA journal_mode = 'mvcc'.
        var path = CreateDatabasePath(nameof(ManagedFtsUpstreamIntegrationTests));
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var connection = database.Connect();
            Execute(connection, "PRAGMA journal_mode = 'mvcc'");
            Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
            Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body)");
            Execute(connection, "INSERT INTO docs VALUES (1, 'alpha beta'), (2, 'gamma'), (3, 'beta delta')");

            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'beta') ORDER BY id")
                .Should().Equal(1, 3);
            QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'gamma')")
                .Should().Equal(2);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Port of <c>test_fts_create_destroy</c> (mod.rs:483).</summary>
    [Test]
    public void CreateDestroy()
    {
        // Upstream drives FtsIndexMethod.attach()/create()/destroy() directly and asserts a Tantivy
        // "fts_dir" directory table appears and disappears. The directory table is Tantivy storage
        // with no managed equivalent, so that assertion is dropped; the SQL-observable lifecycle
        // (the schema returns to exactly the base table after destroy) is ported via CREATE/DROP INDEX.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");

        IReadOnlyList<string> SchemaRows() => QueryTexts(
            connection,
            "SELECT name FROM sqlite_master WHERE type='table' OR type='index'");

        SchemaRows().Should().Equal("docs");

        Execute(connection, "CREATE INDEX fts_docs ON docs USING fts (title, body)");
        SchemaRows().Should().Contain("docs");

        Execute(connection, "DROP INDEX fts_docs");
        var after = SchemaRows();
        after.Should().Contain("docs");
        after.Should().NotContain(static name => name.Contains("fts_dir", StringComparison.Ordinal));
        after.Should().Equal("docs");
    }

    /// <summary>Port of <c>test_fts_insert_query</c> (mod.rs:541).</summary>
    [Test]
    public void InsertQuery()
    {
        // Upstream feeds the cursor directly and queries pattern 0 (fts_score ORDER BY DESC LIMIT);
        // the SQL shape of that pattern is used here.
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_docs ON docs USING fts (title, body)");

        var docs = new (int Id, string Title, string Body)[]
        {
            (1, "Introduction to Rust", "Rust is a systems programming language"),
            (2, "Python Basics", "Python is great for beginners"),
            (3, "Advanced Rust", "Rust has powerful features like ownership"),
            (4, "Database Systems", "Databases store and retrieve data efficiently"),
        };
        foreach (var (id, title, body) in docs)
            Execute(connection, $"INSERT INTO docs VALUES ({id}, '{title}', '{body}')");

        // Intentional divergence: upstream's pattern-0 cursor returns only the ranked hits, so this
        // statement yields 2 rows there. Without a WHERE clause every row qualifies in SQL, and
        // Ahtola keeps that meaning on every plan ("ranking never removes rows",
        // docs/managed-index-methods.md): hits come first by score, then non-matches at 0.0. The
        // Turso result is what the explicit fts_match filter below returns.
        var rust = Query(
            connection,
            "SELECT id, fts_score(title, body, 'Rust') as score FROM docs ORDER BY score DESC LIMIT 10");
        rust.Should().HaveCount(4);
        rust.Take(2).Select(static row => row[0].AsInteger()).Should().BeEquivalentTo([1L, 3L]);
        rust.Take(2).Should().OnlyContain(static row => row[1].Kind == SqlValueKind.Real && row[1].AsReal() > 0.0);
        rust.Skip(2).Should().OnlyContain(static row => row[1].Kind == SqlValueKind.Real && row[1].AsReal() == 0.0);

        var rustHits = Query(
            connection,
            "SELECT id, fts_score(title, body, 'Rust') as score FROM docs WHERE fts_match(title, body, 'Rust') ORDER BY score DESC LIMIT 10");
        rustHits.Select(static row => row[0].AsInteger()).Should().BeEquivalentTo([1L, 3L]);
        rustHits.Should().OnlyContain(static row => row[1].Kind == SqlValueKind.Real && row[1].AsReal() > 0.0);

        QueryIntegers(
                connection,
                "SELECT id, fts_score(title, body, 'Python') as score FROM docs ORDER BY score DESC LIMIT 10")
            .Should().Equal(2, 1, 3, 4);
        QueryIntegers(
                connection,
                "SELECT id, fts_score(title, body, 'Python') as score FROM docs WHERE fts_match(title, body, 'Python') ORDER BY score DESC LIMIT 10")
            .Should().Equal(2);
    }

    /// <summary>Port of <c>test_fts_sql_queries</c> (mod.rs:655).</summary>
    [Test]
    public void SqlQueries()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Database Performance', 'Optimizing database queries is important for performance')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Web Development', 'Modern web applications use JavaScript and APIs')");
        Execute(connection, "INSERT INTO articles VALUES (3, 'Database Design', 'Good database design leads to better performance')");
        Execute(connection, "INSERT INTO articles VALUES (4, 'API Development', 'RESTful APIs are common in web services')");

        var rows = Query(
            connection,
            "SELECT fts_score(title, body, 'database') as score, id, title FROM articles WHERE fts_match(title, body, 'database') ORDER BY score DESC LIMIT 10");
        rows.Should().HaveCount(2);
        IntegerColumn(rows, 1).Should().Contain(1).And.Contain(3);

        rows = Query(
            connection,
            "SELECT fts_score(title, body, 'web') as score, id, title FROM articles WHERE fts_match(title, body, 'web')");
        rows.Should().HaveCount(2);
        IntegerColumn(rows, 1).Should().Contain(2).And.Contain(4);
    }

    /// <summary>Port of <c>test_fts_order_by_and_limit</c> (mod.rs:712).</summary>
    [Test]
    public void OrderByAndLimit()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_notes ON notes USING fts (title, body)");
        Execute(connection, "INSERT INTO notes VALUES (1, 'test', 'This is a test document')");
        Execute(connection, "INSERT INTO notes VALUES (2, 'test test', 'test test test')");
        Execute(connection, "INSERT INTO notes VALUES (3, 'another', 'Another document without the keyword')");
        Execute(connection, "INSERT INTO notes VALUES (4, 'test again', 'The test word appears in test')");

        var rows = Query(
            connection,
            "SELECT fts_score(title, body, 'test') as score, id FROM notes WHERE fts_match(title, body, 'test') ORDER BY score DESC LIMIT 2");
        rows.Should().HaveCount(2);
        rows[0][0].Kind.Should().Be(SqlValueKind.Real);
        rows[1][0].Kind.Should().Be(SqlValueKind.Real);
        rows[0][0].AsReal().Should().BeGreaterThanOrEqualTo(rows[1][0].AsReal(), "results should be ordered by score DESC");

        rows = Query(
            connection,
            "SELECT fts_score(title, body, 'test') as score, id FROM notes WHERE fts_match(title, body, 'test') ORDER BY score DESC");
        rows.Should().HaveCount(3);
        var scores = RealColumn(rows, 0);
        for (var i = 1; i < scores.Count; i++)
            scores[i - 1].Should().BeGreaterThanOrEqualTo(scores[i], "scores should be in descending order");
    }

    /// <summary>Port of <c>test_fts_limit_zero_and_negative</c> (mod.rs:774).</summary>
    [Test]
    public void LimitZeroAndNegative()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'hello world', 'this is a test')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'another', 'hello again')");
        Execute(connection, "INSERT INTO articles VALUES (3, 'no match', 'something else')");

        Query(connection, "SELECT fts_score(title, body, 'hello') as score FROM articles ORDER BY score DESC LIMIT 0")
            .Should().BeEmpty();
        // Intentional divergence (see InsertQuery): upstream returns only the 2 hits; without a WHERE
        // clause Ahtola returns every row, hits first, the non-match at 0.0.
        QueryReals(connection, "SELECT fts_score(title, body, 'hello') as score FROM articles ORDER BY score DESC LIMIT -1")
            .Should().HaveCount(3).And.Subject.Last().Should().Be(0.0);
        Query(connection, "SELECT fts_score(title, body, 'hello') as score FROM articles WHERE fts_match(title, body, 'hello') ORDER BY score DESC LIMIT -1")
            .Should().HaveCount(2);
        Query(connection, "SELECT fts_score(title, body, 'hello') as score FROM articles WHERE fts_match(title, body, 'hello') ORDER BY score DESC")
            .Should().HaveCount(2);
    }

    /// <summary>Port of <c>test_fts_function_recognition</c> (mod.rs:813).</summary>
    [Test]
    public void FunctionRecognition()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, author TEXT, category TEXT, title TEXT, body TEXT, views INTEGER)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Alice', 'tech', 'Rust Programming Guide', 'Learn Rust from scratch', 100)");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Bob', 'tech', 'Python Basics', 'Introduction to Python', 200)");
        Execute(connection, "INSERT INTO articles VALUES (3, 'Alice', 'science', 'Rust in Nature', 'Oxidation and rust formation', 50)");
        Execute(connection, "INSERT INTO articles VALUES (4, 'Charlie', 'tech', 'Advanced Rust Patterns', 'Rust ownership and lifetimes', 300)");

        var rows = Query(
            connection,
            "SELECT id, author, title, category, views, fts_score(title, body, 'Rust') as score FROM articles WHERE fts_match(title, body, 'Rust')");
        rows.Should().HaveCount(3);
        IntegerColumn(rows, 0).Should().Contain(1).And.Contain(3).And.Contain(4);

        rows = Query(
            connection,
            "SELECT id, title, views FROM articles WHERE fts_match(title, body, 'Rust') AND author = 'Alice'");
        rows.Should().HaveCount(2);
        IntegerColumn(rows, 0).Should().Contain(1).And.Contain(3);

        rows = Query(
            connection,
            "SELECT fts_score(title, body, 'Rust') as score, id, title, author FROM articles WHERE fts_match(title, body, 'Rust') AND category = 'tech' ORDER BY score DESC");
        rows.Should().HaveCount(2);
        var scores = RealColumn(rows, 0);
        scores.Should().HaveCount(2);
        scores[0].Should().BeGreaterThanOrEqualTo(scores[1]);

        rows = Query(connection, "SELECT id, author, views FROM articles WHERE fts_match(title, body, 'Python')");
        rows.Should().HaveCount(1);
        rows[0][0].Kind.Should().Be(SqlValueKind.Integer);
        rows[0][0].AsInteger().Should().Be(2);
    }

    /// <summary>Port of <c>test_fts_flexible_query_patterns</c> (mod.rs:911).</summary>
    [Test]
    public void FlexibleQueryPatterns()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, author TEXT, category TEXT, title TEXT, body TEXT, created_at INTEGER)");
        Execute(connection, "CREATE INDEX fts_docs ON docs USING fts (title, body)");
        Execute(connection, "INSERT INTO docs VALUES (1, 'Alice', 'tech', 'Rust Guide', 'Learn Rust programming', 1000)");
        Execute(connection, "INSERT INTO docs VALUES (2, 'Bob', 'tech', 'Python Guide', 'Learn Python basics', 2000)");
        Execute(connection, "INSERT INTO docs VALUES (3, 'Alice', 'science', 'Rust Chemistry', 'Rust and oxidation', 3000)");
        Execute(connection, "INSERT INTO docs VALUES (4, 'Charlie', 'tech', 'Advanced Rust', 'Rust patterns and idioms', 4000)");
        Execute(connection, "INSERT INTO docs VALUES (5, 'Alice', 'tech', 'More Rust', 'Even more Rust content', 5000)");

        // Test 1: specific columns.
        Query(connection, "SELECT id, title FROM docs WHERE fts_match(title, body, 'Rust')").Should().HaveCount(4);

        // Test 2: ORDER BY non-score column ASC.
        var rows = Query(connection, "SELECT id, title FROM docs WHERE fts_match(title, body, 'Rust') ORDER BY id ASC");
        rows.Should().HaveCount(4);
        IntegerColumn(rows, 0).Should().Equal(1, 3, 4, 5);

        // Test 3: ORDER BY non-score column DESC.
        rows = Query(connection, "SELECT id, created_at FROM docs WHERE fts_match(title, body, 'Rust') ORDER BY created_at DESC");
        rows.Should().HaveCount(4);
        IntegerColumn(rows, 1).Should().Equal(5000, 4000, 3000, 1000);

        // Test 4: extra WHERE conditions.
        rows = Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Rust') AND created_at >= 3000 AND author = 'Alice'");
        rows.Should().HaveCount(2);
        IntegerColumn(rows, 0).Should().Contain(3).And.Contain(5);

        // Test 5: LIMIT with non-pattern columns.
        Query(connection, "SELECT id, author FROM docs WHERE fts_match(title, body, 'Rust') LIMIT 2").Should().HaveCount(2);

        // Test 6: computed expressions.
        rows = Query(connection, "SELECT id, author || ' wrote ' || title as description FROM docs WHERE fts_match(title, body, 'Python')");
        rows.Should().HaveCount(1);
        rows[0][1].Kind.Should().Be(SqlValueKind.Text);
        rows[0][1].AsText().Should().Be("Bob wrote Python Guide");

        // Test 7: score with extra columns and WHERE.
        rows = Query(connection, "SELECT fts_score(title, body, 'Rust') as score, id, author, category FROM docs WHERE fts_match(title, body, 'Rust') AND category = 'tech'");
        rows.Should().HaveCount(3);
        IntegerColumn(rows, 1).Should().Contain(1).And.Contain(4).And.Contain(5);
        foreach (var row in rows)
        {
            row[0].Kind.Should().Be(SqlValueKind.Real, "expected real score");
            row[0].AsReal().Should().BeGreaterThan(0.0);
        }

        // Test 8: multiple SELECT expressions with score.
        rows = Query(connection, "SELECT id * 10 as id_times_ten, fts_score(title, body, 'Rust') as score FROM docs WHERE fts_match(title, body, 'Rust')");
        rows.Should().HaveCount(4);
        IntegerColumn(rows, 0).Should().Contain(10).And.Contain(30).And.Contain(40).And.Contain(50);
    }

    /// <summary>Port of <c>test_fts_tokenizer_configuration</c> (mod.rs:1062).</summary>
    [Test]
    public void TokenizerConfiguration()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE docs_default(id INTEGER PRIMARY KEY, content TEXT)");
        Execute(connection, "CREATE INDEX fts_default ON docs_default USING fts (content)");
        Execute(connection, "INSERT INTO docs_default VALUES (1, 'Hello World')");
        Execute(connection, "INSERT INTO docs_default VALUES (2, 'hello there')");
        Query(connection, "SELECT id FROM docs_default WHERE fts_match(content, 'hello')").Should().HaveCount(2);

        Execute(connection, "CREATE TABLE docs_raw(id INTEGER PRIMARY KEY, tag TEXT)");
        Execute(connection, "CREATE INDEX fts_raw ON docs_raw USING fts (tag) WITH (tokenizer = 'raw')");
        Execute(connection, "INSERT INTO docs_raw VALUES (1, 'user-123')");
        Execute(connection, "INSERT INTO docs_raw VALUES (2, 'user-456')");
        Execute(connection, "INSERT INTO docs_raw VALUES (3, 'admin-123')");
        QueryIntegers(connection, "SELECT id FROM docs_raw WHERE fts_match(tag, 'user-123')").Should().Equal(1);
        Query(connection, "SELECT id FROM docs_raw WHERE fts_match(tag, 'user')").Should().BeEmpty();

        Execute(connection, "CREATE TABLE docs_simple(id INTEGER PRIMARY KEY, content TEXT)");
        Execute(connection, "CREATE INDEX fts_simple ON docs_simple USING fts (content) WITH (tokenizer = 'simple')");
        Execute(connection, "INSERT INTO docs_simple VALUES (1, 'Hello World')");
        Execute(connection, "INSERT INTO docs_simple VALUES (2, 'HELLO there')");
        Query(connection, "SELECT id FROM docs_simple WHERE fts_match(content, 'Hello')").Should().NotBeEmpty();
    }

    /// <summary>Port of <c>test_fts_invalid_tokenizer_rejected</c> (mod.rs:1140).</summary>
    [Test]
    public void InvalidTokenizerRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, content TEXT)");
        ShouldThrow(connection, "CREATE INDEX fts_docs ON docs USING fts (content) WITH (tokenizer = 'invalid_tokenizer')");
    }

    /// <summary>Port of <c>test_fts_ngram_tokenizer</c> (mod.rs:1157).</summary>
    [Test]
    public void NgramTokenizer()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT)");
        Execute(connection, "CREATE INDEX fts_products ON products USING fts (name) WITH (tokenizer = 'ngram')");
        Execute(connection, "INSERT INTO products VALUES (1, 'iPhone 15 Pro')");
        Execute(connection, "INSERT INTO products VALUES (2, 'Samsung Galaxy')");
        Execute(connection, "INSERT INTO products VALUES (3, 'Google Pixel')");

        Query(connection, "SELECT id FROM products WHERE fts_match(name, 'Pho')").Should().NotBeEmpty();
        QueryIntegers(connection, "SELECT id FROM products WHERE fts_match(name, 'pho')").Should().Equal(1);
        Query(connection, "SELECT id FROM products WHERE fts_match(name, 'Gal')").Should().NotBeEmpty();
    }

    /// <summary>Port of <c>fts_with_clause_rejects_typo_key</c> (mod.rs:1207).</summary>
    [Test]
    public void WithClauseRejectsTypoKey()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        ShouldThrow(connection, "CREATE INDEX fts_docs ON docs USING fts (body) WITH (tokenzier = 'ngram')");
    }

    /// <summary>Port of <c>fts_with_clause_rejects_unknown_key</c> (mod.rs:1224).</summary>
    [Test]
    public void WithClauseRejectsUnknownKey()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        ShouldThrow(
            connection,
            "CREATE INDEX fts_docs ON docs USING fts (body) WITH (tokenizer = 'ngram', completely_bogus_key = 42)");
    }

    /// <summary>Port of <c>fts_with_clause_rejects_duplicate_key_across_casings</c> (mod.rs:1243).</summary>
    [Test]
    public void WithClauseRejectsDuplicateKeyAcrossCasings()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        ShouldThrow(
            connection,
            "CREATE INDEX fts_docs ON docs USING fts (body) WITH (tokenizer = 'ngram', TOKENIZER = 'raw')");
    }

    /// <summary>Port of <c>fts_with_clause_treats_keys_case_insensitively</c> (mod.rs:1262).</summary>
    [Test]
    public void WithClauseTreatsKeysCaseInsensitively()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX fts_docs ON docs USING fts (body) WITH (TOKENIZER = 'ngram')");
        Execute(connection, "INSERT INTO docs VALUES (1, 'alpha')");
        Query(connection, "SELECT id FROM docs WHERE fts_match(body, 'al')")
            .Should().HaveCount(1, "TOKENIZER = 'ngram' was accepted but ignored: the index got the default tokenizer");
    }

    /// <summary>Port of <c>fts_with_clause_configures_ngram_window</c> (mod.rs:1289).</summary>
    [Test]
    public void WithClauseConfiguresNgramWindow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE narrow(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX fts_narrow ON narrow USING fts (body) WITH (tokenizer = 'ngram', min_gram = 1, max_gram = 3)");
        Execute(connection, "INSERT INTO narrow VALUES (1, 'alpha')");
        Query(connection, "SELECT id FROM narrow WHERE fts_match(body, 'a')")
            .Should().HaveCount(1, "min_gram = 1 was ignored: a 1-character term did not match");

        Execute(connection, "CREATE TABLE wide(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX fts_wide ON wide USING fts (body) WITH (tokenizer = 'ngram')");
        Execute(connection, "INSERT INTO wide VALUES (1, 'alpha')");
        Query(connection, "SELECT id FROM wide WHERE fts_match(body, 'a')")
            .Should().BeEmpty("default ngram window unexpectedly matched a 1-character term");
    }

    /// <summary>Port of <c>fts_with_clause_rejects_bad_ngram_window</c> (mod.rs:1328).</summary>
    [Test]
    public void WithClauseRejectsBadNgramWindow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");

        var accepted = new List<string>();
        foreach (var clause in new[]
                 {
                     "WITH (tokenizer = 'ngram', min_gram = 0)",
                     "WITH (tokenizer = 'ngram', min_gram = 'one')",
                     "WITH (tokenizer = 'ngram', min_gram = 4)",
                     "WITH (tokenizer = 'ngram', min_gram = 3, max_gram = 2)",
                     "WITH (tokenizer = 'raw', min_gram = 1)",
                     "WITH (max_gram = 4)",
                 })
        {
            try
            {
                Execute(connection, $"CREATE INDEX fts_docs ON docs USING fts (body) {clause}");
                // Accepted: record it, and drop the index so the next clause is judged on its own.
                accepted.Add(clause);
                Execute(connection, "DROP INDEX fts_docs");
            }
            catch (EmbeddedSqlException)
            {
            }
        }

        accepted.Should().BeEmpty("every bad ngram window must be rejected at CREATE INDEX time");
    }

    /// <summary>Port of <c>test_fts_highlight_basic</c> (mod.rs:1357).</summary>
    [Test]
    public void HighlightBasic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        QueryTexts(connection, "SELECT fts_highlight('The quick brown fox', '<b>', '</b>', 'quick')")
            .Should().Equal("The <b>quick</b> brown fox");
        QueryTexts(connection, "SELECT fts_highlight('hello world hello', '[', ']', 'hello')")
            .Should().Equal("[hello] world [hello]");
        QueryTexts(connection, "SELECT fts_highlight('Hello World', '<em>', '</em>', 'hello')")
            .Should().Equal("<em>Hello</em> World");
        QueryTexts(connection, "SELECT fts_highlight('The quick brown fox', '<b>', '</b>', 'zebra')")
            .Should().Equal("The quick brown fox");
        QueryTexts(connection, "SELECT fts_highlight('Some text here', '<b>', '</b>', '')")
            .Should().Equal("Some text here");
        QueryTexts(connection, "SELECT fts_highlight('Hello world', 'Goodbye moon', '<b>', '</b>', 'world')")
            .Should().Equal("Hello <b>world</b> Goodbye moon");
    }

    /// <summary>Port of <c>test_fts_highlight_with_fts_query</c> (mod.rs:1443).</summary>
    [Test]
    public void HighlightWithFtsQuery()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Database Design', 'Learn about database optimization and query performance')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Web Development', 'Building modern web applications with databases')");

        var rows = Query(
            connection,
            "SELECT id, fts_highlight(body, '<mark>', '</mark>', 'database') as highlighted FROM articles WHERE fts_match(title, body, 'database')");
        rows.Should().NotBeEmpty();
        rows.Should().Contain(
            static row => row[1].Kind == SqlValueKind.Text
                && row[1].AsText().Contains("<mark>", StringComparison.Ordinal)
                && row[1].AsText().Contains("</mark>", StringComparison.Ordinal),
            "expected highlighted text with <mark> tags");
    }

    /// <summary>Port of <c>test_fts_highlight_null_handling</c> (mod.rs:1488).</summary>
    [Test]
    public void HighlightNullHandling()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        QueryTexts(connection, "SELECT fts_highlight(NULL, 'some text', '<b>', '</b>', 'text')")
            .Should().Equal("some <b>text</b>");

        var rows = Query(connection, "SELECT fts_highlight('text', '<b>', '</b>', NULL)");
        rows.Should().HaveCount(1);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);

        rows = Query(connection, "SELECT fts_highlight('text', NULL, '</b>', 'query')");
        rows.Should().HaveCount(1);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);

        rows = Query(connection, "SELECT fts_highlight('text', '<b>', NULL, 'query')");
        rows.Should().HaveCount(1);
        rows[0][0].Kind.Should().Be(SqlValueKind.Null);
    }

    /// <summary>Port of <c>test_fts_field_weights</c> (mod.rs:1525).</summary>
    [Test]
    public void FieldWeights()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_weighted ON articles USING fts (title, body) WITH (weights='title=2.0,body=1.0')");
        Execute(connection, "INSERT INTO articles VALUES (1, 'rust programming', 'learn python programming')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'python basics', 'rust is fast')");

        var rows = Query(
            connection,
            "SELECT id, fts_score(title, body, 'rust') as score FROM articles WHERE fts_match(title, body, 'rust') ORDER BY score DESC");
        rows.Should().HaveCount(2);
        rows[0][0].AsInteger().Should().Be(1);
        rows[1][0].AsInteger().Should().Be(2);
        rows[0][1].Kind.Should().Be(SqlValueKind.Real);
        rows[1][1].Kind.Should().Be(SqlValueKind.Real);
        rows[0][1].AsReal().Should().BeGreaterThan(
            rows[1][1].AsReal(),
            "title match (boosted 2x) should score higher than body match");
    }

    /// <summary>Port of <c>test_fts_invalid_weights_rejected</c> (mod.rs:1582).</summary>
    [Test]
    public void InvalidWeightsRejected()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");

        ShouldThrow(connection, "CREATE INDEX fts_bad ON docs USING fts (title, body) WITH (weights='unknown=2.0')");
        ShouldThrow(connection, "CREATE INDEX fts_bad2 ON docs USING fts (title) WITH (weights='title=abc')");
        ShouldThrow(connection, "CREATE INDEX fts_bad3 ON docs USING fts (title) WITH (weights='title=-1.0')");
        ShouldThrow(connection, "CREATE INDEX fts_bad4 ON docs USING fts (title) WITH (weights='title2.0')");
    }

    /// <summary>Port of <c>test_fts_query_insert_query_no_panic</c> (mod.rs:1618).</summary>
    [Test]
    public void QueryInsertQueryNoPanic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Rust Programming', 'Rust is a systems language')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Python Guide', 'Python is easy to learn')");

        Query(connection, "SELECT * FROM articles WHERE fts_match(title, body, 'Rust')").Should().HaveCount(1);
        Query(connection, "SELECT * FROM articles WHERE fts_match(title, body, 'Python')").Should().HaveCount(1);
        Query(connection, "SELECT * FROM articles WHERE fts_match(title, body, 'programming')").Should().HaveCount(1);

        Execute(connection, "INSERT INTO articles VALUES (3, 'Go Tutorial', 'Go is great for concurrency')");

        Query(connection, "SELECT * FROM articles WHERE fts_match(title, body, 'Go')").Should().HaveCount(1);
        Query(connection, "SELECT * FROM articles WHERE fts_match(title, body, 'Rust')").Should().HaveCount(1);
    }

    /// <summary>Port of <c>test_fts_comprehensive_lifecycle</c> (mod.rs:1683).</summary>
    [Test]
    public void ComprehensiveLifecycle()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, category TEXT, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_docs ON docs USING fts (title, body)");

        var categories = new[] { "tech", "science", "business", "entertainment" };
        var techTerms = new[] { "Rust", "Python", "JavaScript", "programming", "software", "database" };
        var scienceTerms = new[] { "physics", "chemistry", "biology", "research", "experiment", "discovery" };
        var businessTerms = new[] { "market", "investment", "startup", "revenue", "growth", "strategy" };
        var entertainmentTerms = new[] { "movie", "music", "concert", "festival", "celebrity", "streaming" };

        for (var i = 1; i <= 100; i++)
        {
            var category = categories[(i - 1) % 4];
            var terms = category switch
            {
                "tech" => techTerms,
                "science" => scienceTerms,
                "business" => businessTerms,
                _ => entertainmentTerms,
            };
            var term1 = terms[(i - 1) % terms.Length];
            var term2 = terms[i % terms.Length];
            var title = $"{term1} Article {i}";
            var body = $"This is article {i} about {term1} and {term2}. More content here.";
            Execute(connection, $"INSERT INTO docs VALUES ({i}, '{category}', '{title}', '{body}')");
        }

        // 2. Initial state.
        var rows = Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Rust')");
        rows.Should().NotBeEmpty("should find Rust documents");
        var rustCountInitial = rows.Count;
        Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Python')")
            .Should().NotBeEmpty("should find Python documents");
        Query(connection, "SELECT fts_score(title, body, 'programming') as score, id FROM docs WHERE fts_match(title, body, 'programming') ORDER BY score DESC LIMIT 10")
            .Should().NotBeEmpty("should find programming documents");

        // 3. Insert.
        Execute(connection, "INSERT INTO docs VALUES (101, 'tech', 'Advanced Rust Techniques', 'Deep dive into Rust programming patterns and idioms')");
        Execute(connection, "INSERT INTO docs VALUES (102, 'tech', 'Rust Memory Safety', 'Exploring Rust ownership and borrowing mechanisms')");
        Execute(connection, "INSERT INTO docs VALUES (103, 'science', 'Rust Prevention', 'Studying corrosion and metal oxidation')");

        // 4. Inserts are indexed.
        Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Rust')")
            .Count.Should().BeGreaterThanOrEqualTo(rustCountInitial + 2, "should find more Rust documents after insert");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'ownership borrowing')")
            .Should().Equal(new long[] { 102 }, "should find the memory safety document");

        // 5/6. Delete is reflected.
        Execute(connection, "DELETE FROM docs WHERE id = 101");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Advanced Techniques')")
            .Should().BeEmpty();
        Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'ownership')")
            .Should().HaveCount(1, "document 102 should still be findable after deleting 101");

        // 7/8. Large update.
        Execute(connection, "UPDATE docs SET title = 'Updated ' || title WHERE category = 'tech'");
        Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Python')")
            .Should().NotBeEmpty("should still find Python documents after update");
        _ = Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'science')");

        foreach (var row in Query(connection, "SELECT fts_score(title, body, 'database') as score, id FROM docs WHERE fts_match(title, body, 'database') ORDER BY score DESC"))
        {
            row[0].Kind.Should().BeOneOf([SqlValueKind.Real, SqlValueKind.Integer], "expected numeric score");
            if (row[0].Kind == SqlValueKind.Real)
                row[0].AsReal().Should().BeGreaterThanOrEqualTo(0.0);
        }

        rows = Query(connection, "SELECT fts_score(title, body, 'Rust') as score, id, category FROM docs WHERE fts_match(title, body, 'Rust') AND category = 'tech' ORDER BY score DESC LIMIT 5");
        rows.Should().NotBeEmpty("should find tech documents about Rust with complex query");
        foreach (var row in rows)
            row[2].AsText().Should().Be("tech");
    }

    /// <summary>Port of <c>test_fts_with_explicit_transactions</c> (mod.rs:1884).</summary>
    [Test]
    public void WithExplicitTransactions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, content TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, content)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Rust Basics', 'Introduction to Rust programming')");
        Query(connection, "SELECT id FROM articles WHERE fts_match(title, content, 'Rust')").Should().HaveCount(1);

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Advanced Rust', 'Rust ownership and lifetimes')");
        Execute(connection, "INSERT INTO articles VALUES (3, 'Python Guide', 'Python for beginners')");
        Execute(connection, "COMMIT");

        Query(connection, "SELECT id FROM articles WHERE fts_match(title, content, 'Rust')")
            .Should().HaveCount(2, "should find 2 Rust articles after commit");
        Query(connection, "SELECT id FROM articles WHERE fts_match(title, content, 'Python')")
            .Should().HaveCount(1, "should find 1 Python article after commit");

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO articles VALUES (4, 'Go Guide', 'Go concurrency patterns')");
        Execute(connection, "ROLLBACK");

        Query(connection, "SELECT id FROM articles WHERE fts_match(title, content, 'Go')")
            .Should().BeEmpty("should not find Go article after rollback");
        Query(connection, "SELECT id FROM articles WHERE fts_match(title, content, 'Rust')")
            .Should().HaveCount(2, "Rust articles should still be indexed after rollback");
    }

    /// <summary>Port of <c>test_fts_optimize_index</c> (mod.rs:1959).</summary>
    [Test]
    public void OptimizeIndex()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_docs ON docs USING fts (title, body)");
        for (var i = 0; i < 10; i++)
            Execute(connection, $"INSERT INTO docs VALUES ({i}, 'Document {i}', 'Content about topic {i} with keywords')");

        Query(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'Document')")
            .Should().HaveCount(10, "should find all 10 documents");

        // Dropped (Tantivy-internal): fts_test_stats segment_count == Some(3) before OPTIMIZE,
        // segment_count == Some(1) after, and storage_file_count strictly decreasing. The managed
        // index has no segments or directory files.
        Execute(connection, "OPTIMIZE INDEX fts_docs");

        Query(connection, "SELECT id FROM docs WHERE (title, body) MATCH 'Document'")
            .Should().HaveCount(10, "should still find all 10 documents after optimize");
        Query(connection, "SELECT id FROM docs WHERE (title, body) MATCH 'topic'")
            .Should().HaveCount(10, "should find all documents with 'topic'");
    }

    /// <summary>Port of <c>test_fts_optimize_all_indexes</c> (mod.rs:2041).</summary>
    [Test]
    public void OptimizeAllIndexes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT)");
        Execute(connection, "CREATE TABLE posts(id INTEGER PRIMARY KEY, content TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title)");
        Execute(connection, "CREATE INDEX fts_posts ON posts USING fts (content)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Rust Programming')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Python Guide')");
        Execute(connection, "INSERT INTO posts VALUES (1, 'Learning Rust is fun')");
        Execute(connection, "INSERT INTO posts VALUES (2, 'Advanced Rust patterns')");

        Execute(connection, "OPTIMIZE INDEX");

        Query(connection, "SELECT id FROM articles WHERE fts_match(title, 'Rust')")
            .Should().HaveCount(1, "should find Rust article");
        Query(connection, "SELECT id FROM posts WHERE content MATCH 'Rust'")
            .Should().HaveCount(2, "should find both Rust posts");
    }

    /// <summary>Port of <c>test_fts_column_order_agnostic</c> (mod.rs:2084).</summary>
    [Test]
    public void ColumnOrderAgnostic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Database Design', 'Learn about database systems')");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Web Development', 'Building modern web applications')");
        Execute(connection, "INSERT INTO articles VALUES (3, 'SQL Basics', 'Introduction to database and SQL')");

        var standard = QueryIntegers(connection, "SELECT id FROM articles WHERE (title, body) MATCH 'database'");
        standard.Should().HaveCount(2, "standard order should find 2 matches (articles 1 and 3)");
        standard.Should().Contain(1).And.Contain(3);

        var reversed = QueryIntegers(connection, "SELECT id FROM articles WHERE (body, title) MATCH 'database'");
        reversed.Should().HaveCount(2, "reversed column order should find same 2 matches");
        reversed.Should().Contain(1).And.Contain(3);

        Query(connection, "SELECT id, fts_score(body, title, 'database') as score FROM articles WHERE (body, title) MATCH 'database' ORDER BY score DESC")
            .Should().HaveCount(2, "fts_score with reversed columns should work");

        standard.Count.Should().Be(reversed.Count);
        reversed.Should().Contain(standard);
    }

    /// <summary>Port of <c>test_fts_with_join</c> (mod.rs:2178).</summary>
    [Test]
    public void WithJoin()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT, author_id INTEGER)");
        Execute(connection, "CREATE TABLE authors(id INTEGER PRIMARY KEY, name TEXT)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO authors VALUES (1, 'Alice')");
        Execute(connection, "INSERT INTO authors VALUES (2, 'Bob')");
        Execute(connection, "INSERT INTO authors VALUES (3, 'Charlie')");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Database Design', 'Learn about database systems', 1)");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Web Development', 'Building modern web applications', 2)");
        Execute(connection, "INSERT INTO articles VALUES (3, 'SQL Basics', 'Introduction to database and SQL', 1)");
        Execute(connection, "INSERT INTO articles VALUES (4, 'API Design', 'RESTful API best practices', 3)");

        var rows = Query(
            connection,
            "SELECT a.id, a.title, u.name FROM articles a JOIN authors u ON a.author_id = u.id WHERE (a.title, a.body) MATCH 'database'");
        rows.Should().HaveCount(2, "should find 2 articles about database (articles 1 and 3)");
        IntegerColumn(rows, 0).Should().Contain(1).And.Contain(3);
        rows.Count(static row => row[2].Kind == SqlValueKind.Text && row[2].AsText() == "Alice")
            .Should().Be(2, "both matching articles should be by Alice");

        rows = Query(
            connection,
            "SELECT a.id, a.title, u.name FROM articles a JOIN authors u ON a.author_id = u.id WHERE (a.title, a.body) MATCH 'web' AND u.name = 'Bob'");
        rows.Should().HaveCount(1, "should find 1 article about web by Bob");
        rows[0][0].AsInteger().Should().Be(2, "should be article 2 (Web Development by Bob)");
    }

    /// <summary>Port of <c>test_fts_with_left_join</c> (mod.rs:2271).</summary>
    [Test]
    public void WithLeftJoin()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE posts(id INTEGER PRIMARY KEY, title TEXT, content TEXT, category_id INTEGER)");
        Execute(connection, "CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT)");
        Execute(connection, "CREATE INDEX fts_posts ON posts USING fts (title, content)");
        Execute(connection, "INSERT INTO categories VALUES (1, 'Technology')");
        Execute(connection, "INSERT INTO categories VALUES (2, 'Science')");
        Execute(connection, "INSERT INTO posts VALUES (1, 'Rust Programming', 'Systems programming with Rust', 1)");
        Execute(connection, "INSERT INTO posts VALUES (2, 'Python Basics', 'Introduction to Python programming', 1)");
        Execute(connection, "INSERT INTO posts VALUES (3, 'Rust in Nature', 'How rust affects metal', 2)");
        Execute(connection, "INSERT INTO posts VALUES (4, 'Uncategorized Rust', 'A post about Rust without category', NULL)");

        var rows = Query(
            connection,
            "SELECT p.id, p.title, c.name FROM posts p LEFT JOIN categories c ON p.category_id = c.id WHERE fts_match(p.title, p.content, 'Rust')");
        rows.Should().HaveCount(3, "should find 3 posts about Rust");
        IntegerColumn(rows, 0).Should().Contain(1).And.Contain(3).And.Contain(4);
        rows.Count(static row => row[2].Kind == SqlValueKind.Null)
            .Should().Be(1, "one post should have NULL category");
    }

    /// <summary>Port of <c>test_fts_join_order_optimization</c> (mod.rs:2343).</summary>
    [Test]
    public void JoinOrderOptimization()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE authors(id INTEGER PRIMARY KEY, name TEXT)");
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT, author_id INTEGER)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        for (var i = 1; i <= 5; i++)
            Execute(connection, $"INSERT INTO authors VALUES ({i}, 'Author{i}')");
        Execute(connection, "ANALYZE");

        for (var i = 1; i <= 50; i++)
        {
            var authorId = (i % 5) + 1;
            var (title, body) = i % 10 == 0
                ? ($"Database Article {i}", "Content about database systems and SQL")
                : ($"General Article {i}", "General content about various topics");
            Execute(connection, $"INSERT INTO articles VALUES ({i}, '{title}', '{body}', {authorId})");
        }

        const string query = "SELECT a.id, a.title, u.name FROM articles a JOIN authors u ON a.author_id = u.id WHERE fts_match(a.title, a.body, 'database')";
        var plan = ExplainDetails(connection, query);

        var tableOrder = new List<string>();
        var hasFtsSearch = false;
        foreach (var detail in plan)
        {
            if (detail.Contains("INDEX METHOD", StringComparison.Ordinal)
                || detail.Contains("fts_articles", StringComparison.Ordinal))
            {
                hasFtsSearch = true;
            }

            if (detail.StartsWith("SCAN ", StringComparison.Ordinal))
                tableOrder.Add(FirstWord(detail["SCAN ".Length..]));
            else if (detail.StartsWith("SEARCH ", StringComparison.Ordinal))
                tableOrder.Add(FirstWord(detail["SEARCH ".Length..]));
            else if (detail.StartsWith("QUERY INDEX METHOD", StringComparison.Ordinal))
                tableOrder.Add("articles");
        }

        hasFtsSearch.Should().BeTrue($"expected FTS index to be used in query plan. Plan details: [{string.Join(" | ", plan)}]");
        tableOrder.Should().HaveCount(2, $"expected 2 tables in join order, got: [{string.Join(", ", tableOrder)}]");
        tableOrder[0].Should().Be("articles", $"expected articles (FTS) to be first in join order, got: [{string.Join(", ", tableOrder)}]");
        tableOrder[1].Should().BeOneOf(["u", "authors"], $"expected authors to be second in join order, got: [{string.Join(", ", tableOrder)}]");

        var rows = Query(connection, query);
        rows.Should().HaveCount(5, "should find 5 articles about database");
        foreach (var row in rows)
        {
            row[2].Kind.Should().Be(SqlValueKind.Text, "expected text for author name");
            row[2].AsText().Should().StartWith("Author");
        }

        const string query2 = "SELECT a.id, a.title, u.name FROM authors u JOIN articles a ON u.id = a.author_id WHERE fts_match(a.title, a.body, 'database')";
        var plan2 = ExplainDetails(connection, query2);
        plan2.Any(static detail => detail.Contains("INDEX METHOD", StringComparison.Ordinal)
                || detail.Contains("fts_articles", StringComparison.Ordinal))
            .Should().BeTrue($"expected FTS index to be used with reversed table order. Plan details: [{string.Join(" | ", plan2)}]");
        Query(connection, query2).Should().HaveCount(5, "should find same 5 articles with reversed table order");
    }

    /// <summary>Port of <c>test_fts_multi_table_join</c> (mod.rs:2497).</summary>
    [Test]
    public void MultiTableJoin()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT)");
        Execute(connection, "CREATE TABLE authors(id INTEGER PRIMARY KEY, name TEXT)");
        Execute(connection, "CREATE TABLE articles(id INTEGER PRIMARY KEY, title TEXT, body TEXT, author_id INTEGER, category_id INTEGER)");
        Execute(connection, "CREATE INDEX fts_articles ON articles USING fts (title, body)");
        Execute(connection, "INSERT INTO categories VALUES (1, 'Technology')");
        Execute(connection, "INSERT INTO categories VALUES (2, 'Science')");
        Execute(connection, "INSERT INTO categories VALUES (3, 'Arts')");
        Execute(connection, "INSERT INTO authors VALUES (1, 'Alice')");
        Execute(connection, "INSERT INTO authors VALUES (2, 'Bob')");
        Execute(connection, "INSERT INTO articles VALUES (1, 'Database Systems', 'Introduction to database management', 1, 1)");
        Execute(connection, "INSERT INTO articles VALUES (2, 'Machine Learning', 'AI and neural networks', 2, 2)");
        Execute(connection, "INSERT INTO articles VALUES (3, 'SQL Performance', 'Optimizing database queries', 1, 1)");
        Execute(connection, "INSERT INTO articles VALUES (4, 'Modern Art', 'Contemporary art movements', 2, 3)");

        var titles = QueryTexts(
            connection,
            "SELECT a.title, u.name, c.name FROM articles a "
            + "JOIN authors u ON a.author_id = u.id "
            + "JOIN categories c ON a.category_id = c.id "
            + "WHERE (a.title, a.body) MATCH 'database'");
        titles.Should().HaveCount(2, "should find 2 articles about database");
        titles.Should().Contain("Database Systems").And.Contain("SQL Performance");
    }

    /// <summary>Port of <c>fts_rolled_back_optimize_does_not_leak_segment_state</c> (mod.rs:2578).</summary>
    [Test]
    public void RolledBackOptimizeDoesNotLeakSegmentState()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, x TEXT, f TEXT, b BLOB)");
        Execute(connection, "CREATE INDEX idx ON t USING fts(f)");
        Execute(connection, "INSERT INTO t(id,x,f,b) VALUES (270323, 'x', 'optimize', X'01'), (-596572, NULL, 'foo', X'02')");

        Execute(connection, "BEGIN");
        Execute(connection, "UPDATE t SET b=X'D9', f='rust token full search text search rollback'");
        Execute(connection, "OPTIMIZE INDEX idx");
        Execute(connection, "ROLLBACK");

        Execute(connection, "INSERT INTO t(id) VALUES (32378), (NULL), (524997)");
        Execute(connection, "DELETE FROM t WHERE x");

        Query(connection, "SELECT id FROM t WHERE f MATCH 'foo'")
            .Should().HaveCount(1, "pre-transaction document must remain searchable");
        Query(connection, "SELECT id FROM t WHERE f MATCH 'rollback'")
            .Should().BeEmpty("rolled-back document must not be searchable");
    }

    /// <summary>Port of <c>fts_rolled_back_automatic_merge_restores_segments</c> (mod.rs:2623).</summary>
    [Test]
    public void RolledBackAutomaticMergeRestoresSegments()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body)");
        for (var id = 0; id < 7; id++)
            Execute(connection, $"INSERT INTO docs VALUES ({id}, 'committed common document {id}')");

        // Dropped (Tantivy-internal): fts_test_stats segment_count == Some(7) here, Some(1) after the
        // in-transaction insert triggers automatic merge, Some(7) after ROLLBACK and Some(1) after the
        // next committed write. The managed index has no segments or merge policy.
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO docs VALUES (7, 'ephemeralrollbacktoken common document')");
        Execute(connection, "ROLLBACK");

        Query(connection, "SELECT id FROM docs WHERE fts_match(body, 'ephemeralrollbacktoken')").Should().BeEmpty();

        Execute(connection, "INSERT INTO docs VALUES (8, 'surviving common document')");
        Query(connection, "SELECT id FROM docs WHERE fts_match(body, 'common')").Should().HaveCount(8);
    }

    /// <summary>Port of <c>fts_reuses_committed_writer_across_insert_statements</c> (mod.rs:2682).</summary>
    [Test]
    public void ReusesCommittedWriterAcrossInsertStatements()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body)");

        // Dropped (Tantivy-internal): fts_attachment_test_stats cached_writer == Some(true) after each
        // INSERT. The managed index has no retained Tantivy writer or directory lock.
        Execute(connection, "INSERT INTO docs VALUES (1, 'first retained writer document')");
        Execute(connection, "INSERT INTO docs VALUES (2, 'second retained writer document')");
        Query(connection, "SELECT id FROM docs WHERE fts_match(body, 'retained')").Should().HaveCount(2);

        Execute(connection, "DROP INDEX docs_fts");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body)");
        Query(connection, "SELECT id FROM docs WHERE fts_match(body, 'retained')").Should().HaveCount(2);
    }

    /// <summary>Port of <c>fts_uncommitted_changes_are_connection_isolated</c> (mod.rs:2735).</summary>
    [Test]
    public void UncommittedChangesAreConnectionIsolated()
    {
        var path = CreateDatabasePath(nameof(ManagedFtsUpstreamIntegrationTests));
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using var writer = database.Connect();
            using var observer = database.Connect();

            Execute(writer, "CREATE TABLE docs(id INTEGER PRIMARY KEY, content TEXT)");
            Execute(writer, "CREATE INDEX docs_fts ON docs USING fts(content)");
            Execute(writer, "INSERT INTO docs VALUES (10, 'charlie'), (13, 'charlie'), (20, 'unrelated')");

            const string query = "SELECT id FROM docs WHERE fts_match(content, 'charlie') ORDER BY id";
            QueryIntegers(observer, query).Should().Equal(10, 13);
            QueryIntegers(writer, query).Should().Equal(
                new long[] { 10, 13 },
                "writer should warm its own cached read state before starting the transaction");

            Execute(writer, "BEGIN");
            Execute(writer, "UPDATE docs SET content = NULL WHERE id = 10");
            Execute(writer, "INSERT INTO docs VALUES (14, 'charlie')");

            QueryIntegers(writer, query).Should().Equal(13, 14);
            QueryIntegers(observer, query).Should().Equal(
                new long[] { 10, 13 },
                "observer must retain its committed FTS snapshot");

            Execute(writer, "ROLLBACK");
            QueryIntegers(writer, query).Should().Equal(10, 13);

            Execute(writer, "INSERT INTO docs VALUES (14, 'charlie')");
            QueryIntegers(observer, query).Should().Equal(
                new long[] { 10, 13, 14 },
                "observer must discard its cached FTS state when the WAL snapshot advances");
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Port of <c>fts_read_cache_is_connection_local_and_bounded</c> (mod.rs:2821).</summary>
    [Test]
    public void ReadCacheIsConnectionLocalAndBounded()
    {
        // Dropped (Tantivy-internal): every upstream assertion reads cursor test_stats
        // (cached_connection_count sequence 1,2,2,2,3,4,4 and cached_bytes <= 192 MiB). The managed
        // index keeps no per-connection Tantivy read cache. What remains SQL-observable is that each
        // of the alternating reader connections, in the same access order, reads the committed rows.
        var path = CreateDatabasePath(nameof(ManagedFtsUpstreamIntegrationTests));
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path);
            using (var setup = database.Connect())
            {
                Execute(setup, "CREATE TABLE docs(id INTEGER PRIMARY KEY, content TEXT)");
                Execute(setup, "CREATE INDEX docs_fts ON docs USING fts(content)");
                Execute(setup, "INSERT INTO docs VALUES (1, 'database document')");
            }

            var readers = Enumerable.Range(0, 5).Select(_ => database.Connect()).ToArray();
            try
            {
                foreach (var readerIndex in new[] { 0, 1, 0, 1, 2, 3, 4 })
                {
                    QueryIntegers(readers[readerIndex], "SELECT id FROM docs WHERE fts_match(content, 'database')")
                        .Should().Equal(new long[] { 1 }, $"reader {readerIndex}");
                }
            }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    /// <summary>Port of <c>fts_streaming_dml_collects_stable_rowids</c> (mod.rs:2870).</summary>
    [Test]
    public void StreamingDmlCollectsStableRowids()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, content TEXT)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(content)");
        var values = string.Join(",", Enumerable.Range(0, 32).Select(static id => $"({id}, 'common document {id}')"));
        Execute(connection, $"INSERT INTO docs VALUES {values}");

        Execute(connection, "UPDATE docs SET content = 'updated document' WHERE fts_match(content, 'common')");
        Query(connection, "SELECT id FROM docs WHERE fts_match(content, 'common')").Should().BeEmpty();
        Query(connection, "SELECT id FROM docs WHERE fts_match(content, 'updated')").Should().HaveCount(32);

        Execute(connection, "DELETE FROM docs WHERE fts_match(content, 'updated')");
        Query(connection, "SELECT id FROM docs").Should().BeEmpty();
        Query(connection, "SELECT id FROM docs WHERE fts_match(content, 'updated')").Should().BeEmpty();
    }

    /// <summary>Port of <c>test_fts_fk_cascade_delete_flush</c> (mod.rs:2918).</summary>
    [Test]
    public void FkCascadeDeleteFlush()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys=ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE docs(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE, title TEXT, body TEXT)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(title, body)");
        Execute(connection, "INSERT INTO parent VALUES (1), (2)");
        Execute(connection, "INSERT INTO docs VALUES (10, 1, 'cascade', 'x'), (20, 2, 'keep', 'y')");
        Execute(connection, "DELETE FROM parent WHERE id = 1");

        QueryIntegers(connection, "SELECT id FROM docs ORDER BY id").Should().Equal(20);
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'cascade') ORDER BY id")
            .Should().BeEmpty("cascade-deleted row must be removed from the FTS index");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(title, body, 'keep') ORDER BY id")
            .Should().Equal(20);
    }

    /// <summary>Port of <c>test_fts_trigger_subprogram_flush</c> (mod.rs:2955).</summary>
    [Test]
    public void TriggerSubprogramFlush()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX audit_fts ON audit USING fts (body)");
        Execute(
            connection,
            "CREATE TRIGGER src_trigger AFTER INSERT ON source BEGIN INSERT INTO audit(id, body) VALUES (NEW.id, NEW.body); END");

        Execute(connection, "INSERT INTO source(id, body) VALUES (1, 'alpha'), (2, 'bravo'), (3, 'charlie')");

        foreach (var (term, expectedId) in new[] { ("alpha", 1L), ("bravo", 2L), ("charlie", 3L) })
        {
            QueryIntegers(connection, $"SELECT id FROM audit WHERE fts_match(body, '{term}')")
                .Should().Equal(
                    new[] { expectedId },
                    $"FTS index updated by the trigger should return id {expectedId} for '{term}'");
        }
    }

    /// <summary>Port of <c>test_fts_trigger_update_subprogram_flush</c> (mod.rs:2991).</summary>
    [Test]
    public void TriggerUpdateSubprogramFlush()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE message(id INTEGER PRIMARY KEY, tag TEXT)");
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts (body)");
        Execute(connection, "INSERT INTO docs(id, body) VALUES (1, 'original text')");
        Execute(
            connection,
            "CREATE TRIGGER msg_update_trg AFTER UPDATE ON message BEGIN UPDATE docs SET body = 'updated text' WHERE id = NEW.id; END");
        Execute(connection, "INSERT INTO message(id, tag) VALUES (1, 'aba')");
        Execute(connection, "UPDATE message SET tag = 'bab' WHERE id = 1");

        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'updated')")
            .Should().Equal(new long[] { 1 }, "re-indexed doc must be found by its NEW term");
        QueryIntegers(connection, "SELECT id FROM docs WHERE fts_match(body, 'original')")
            .Should().BeEmpty("the OLD term must be gone from the FTS index after the in-subprogram UPDATE");
    }

    /// <summary>Port of <c>test_fts_trigger_abort_not_flushed</c> (mod.rs:3035).</summary>
    [Test]
    public void TriggerAbortNotFlushed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE log(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX log_fts ON log USING fts (body)");
        Execute(connection, "CREATE TABLE uniq_t(x INTEGER UNIQUE)");
        Execute(connection, "INSERT INTO uniq_t(x) VALUES (1)");
        Execute(
            connection,
            "CREATE TRIGGER t_trg AFTER INSERT ON t BEGIN INSERT INTO log(id, body) VALUES (NEW.id, 'ghost'); INSERT INTO uniq_t(x) VALUES (1); END");

        ShouldThrow(connection, "INSERT INTO t(id) VALUES (1)");

        QueryIntegers(connection, "SELECT id FROM t").Should().BeEmpty("aborted top-level insert must roll back");
        QueryIntegers(connection, "SELECT id FROM log").Should().BeEmpty("aborted trigger insert must roll back");
        QueryIntegers(connection, "SELECT id FROM log WHERE fts_match(body, 'ghost')")
            .Should().BeEmpty("aborted subprogram's FTS doc must not be indexed");
    }

    /// <summary>
    /// SQL-observable port of <c>query_limit_is_exact_and_bounded_by_live_documents</c>
    /// (core/index_method/fts.rs:4226): <c>bounded_query_limit</c> treats no limit and a negative
    /// limit as "every live document", clamps an oversized limit to the live count, and honours an
    /// exact or zero limit.
    /// </summary>
    [Test]
    public void QueryLimitIsExactAndBoundedByLiveDocuments()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE docs(id INTEGER PRIMARY KEY, body TEXT)");
        Execute(connection, "CREATE INDEX docs_fts ON docs USING fts(body)");

        // No live documents: bounded_query_limit(None, 0) == 0.
        Query(connection, "SELECT fts_score(body, 'database') as score FROM docs ORDER BY score DESC LIMIT -1")
            .Should().BeEmpty();

        var values = string.Join(",", Enumerable.Range(1, 37).Select(static id => $"({id}, 'database {id}')"));
        Execute(connection, $"INSERT INTO docs VALUES {values}");

        const string ranked = "SELECT fts_score(body, 'database') as score FROM docs ORDER BY score DESC LIMIT ";
        Query(connection, ranked + "-1").Should().HaveCount(37);
        Query(connection, ranked + long.MaxValue.ToString(CultureInfo.InvariantCulture)).Should().HaveCount(37);
        Query(connection, ranked + "12").Should().HaveCount(12);
        Query(connection, ranked + "0").Should().BeEmpty();
    }

    /// <summary>
    /// Port of <c>fts_index_maintenance_model_fuzz</c> (tests/fuzz/fts.rs:99) as a deterministic
    /// seeded differential test against the same in-memory model oracle upstream uses.
    /// </summary>
    [TestCase(1UL)]
    [TestCase(0x5EEDUL)]
    [TestCase(20260925UL)]
    [TestCase(0xC0FFEEUL)]
    [TestCase(0xDEADBEEFUL)]
    public void IndexMaintenanceModelFuzz(ulong seed)
    {
        FtsModelFuzz.Run(seed, iterations: 300);
    }

    private static List<long> IntegerColumn(IReadOnlyList<SqlValue[]> rows, int column)
        => rows.Where(row => row[column].Kind == SqlValueKind.Integer)
            .Select(row => row[column].AsInteger())
            .ToList();

    private static List<double> RealColumn(IReadOnlyList<SqlValue[]> rows, int column)
        => rows.Where(row => row[column].Kind == SqlValueKind.Real)
            .Select(row => row[column].AsReal())
            .ToList();

    private static List<string> ExplainDetails(EmbeddedConnection connection, string sql)
        => Query(connection, "EXPLAIN QUERY PLAN " + sql)
            .Where(static row => row[3].Kind == SqlValueKind.Text)
            .Select(static row => row[3].AsText())
            .ToList();

    private static string FirstWord(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>The model-based churn fuzzer from <c>tests/fuzz/fts.rs</c>, driven by a fixed seed.</summary>
    private static class FtsModelFuzz
    {
        private static readonly string[] Tokens =
            ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];

        public static void Run(ulong seed, int iterations)
        {
            var rng = new SplitMix64(seed);
            var path = CreateDatabasePath(nameof(ManagedFtsUpstreamIntegrationTests));
            try
            {
                using var database = EmbeddedDatabase.OpenFile(path);
                using var writer = database.Connect();
                using var observer = database.Connect();

                var history = new List<string>
                {
                    "CREATE TABLE docs(id INTEGER PRIMARY KEY, content TEXT)",
                    "CREATE INDEX docs_fts ON docs USING fts(content)",
                };
                Execute(writer, history[0]);
                Execute(writer, history[1]);

                var committed = new SortedDictionary<long, string?>();
                var visible = new SortedDictionary<long, string?>();
                var inTransaction = false;
                var nextId = 1L;

                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    var roll = rng.NextInt(100);
                    if (roll <= 7 && !inTransaction)
                    {
                        Run(writer, "BEGIN", history);
                        inTransaction = true;
                    }
                    else if (roll <= 3 && inTransaction)
                    {
                        Run(writer, "COMMIT", history);
                        committed = new SortedDictionary<long, string?>(visible);
                        inTransaction = false;
                    }
                    else if (roll is >= 4 and <= 7 && inTransaction)
                    {
                        Run(writer, "ROLLBACK", history);
                        visible = new SortedDictionary<long, string?>(committed);
                        inTransaction = false;
                    }
                    else if (roll is >= 8 and <= 37)
                    {
                        var content = RandomContent(rng);
                        Run(writer, $"INSERT INTO docs(id, content) VALUES ({nextId}, {SqlText(content)})", history);
                        visible[nextId] = content;
                        nextId++;
                        if (!inTransaction)
                            committed = new SortedDictionary<long, string?>(visible);
                    }
                    else if (roll is >= 38 and <= 57 && visible.Count > 0)
                    {
                        var id = visible.Keys.ElementAt(rng.NextInt(visible.Count));
                        var content = RandomContent(rng);
                        Run(writer, $"UPDATE docs SET content = {SqlText(content)} WHERE id = {id}", history);
                        visible[id] = content;
                        if (!inTransaction)
                            committed = new SortedDictionary<long, string?>(visible);
                    }
                    else if (roll is >= 58 and <= 69 && visible.Count > 0)
                    {
                        var id = visible.Keys.ElementAt(rng.NextInt(visible.Count));
                        Run(writer, $"DELETE FROM docs WHERE id = {id}", history);
                        visible.Remove(id);
                        if (!inTransaction)
                            committed = new SortedDictionary<long, string?>(visible);
                    }
                    else if (roll is >= 70 and <= 79)
                    {
                        var token = Tokens[rng.NextInt(Tokens.Length)];
                        var content = RandomContent(rng);
                        Run(writer, $"UPDATE docs SET content = {SqlText(content)} WHERE fts_match(content, '{token}')", history);
                        foreach (var id in visible.Keys.ToArray())
                        {
                            if (ContainsToken(visible[id], token))
                                visible[id] = content;
                        }

                        if (!inTransaction)
                            committed = new SortedDictionary<long, string?>(visible);
                    }
                    else if (roll is >= 80 and <= 87)
                    {
                        var token = Tokens[rng.NextInt(Tokens.Length)];
                        Run(writer, $"DELETE FROM docs WHERE fts_match(content, '{token}')", history);
                        foreach (var id in visible.Keys.ToArray())
                        {
                            if (ContainsToken(visible[id], token))
                                visible.Remove(id);
                        }

                        if (!inTransaction)
                            committed = new SortedDictionary<long, string?>(visible);
                    }
                    else if (roll is >= 88 and <= 94)
                    {
                        var beforeSavepoint = new SortedDictionary<long, string?>(visible);
                        Run(writer, "SAVEPOINT fts_fuzz", history);
                        var content = RandomContent(rng);
                        var id = nextId++;
                        Run(writer, $"INSERT INTO docs(id, content) VALUES ({id}, {SqlText(content)})", history);
                        visible[id] = content;

                        if (rng.NextBool())
                        {
                            Run(writer, "ROLLBACK TO fts_fuzz", history);
                            visible = beforeSavepoint;
                        }

                        Run(writer, "RELEASE fts_fuzz", history);
                        if (!inTransaction)
                            committed = new SortedDictionary<long, string?>(visible);
                    }
                    else
                    {
                        Run(writer, "OPTIMIZE INDEX docs_fts", history);
                    }

                    var probe = Tokens[rng.NextInt(Tokens.Length)];
                    AssertMatchesModel(writer, visible, probe, seed, history, "writer");

                    if (iteration % 11 == 0)
                        AssertMatchesModel(observer, inTransaction ? committed : visible, probe, seed, history, "observer");

                    if (iteration % 37 == 0)
                    {
                        foreach (var token in Tokens)
                            AssertMatchesModel(writer, visible, token, seed, history, "writer");
                    }
                }

                if (inTransaction)
                {
                    Run(writer, "ROLLBACK", history);
                    visible = committed;
                }

                foreach (var token in Tokens)
                {
                    AssertMatchesModel(writer, visible, token, seed, history, "writer");
                    AssertMatchesModel(observer, visible, token, seed, history, "observer");
                }
            }
            finally
            {
                DeleteDatabase(path);
            }
        }

        private static string? RandomContent(SplitMix64 rng)
        {
            if (rng.NextInt(12) == 0)
                return null;

            var count = 1 + rng.NextInt(4);
            var pool = (string[])Tokens.Clone();
            for (var i = 0; i < count; i++)
            {
                var j = i + rng.NextInt(pool.Length - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var chosen = pool.Take(count).ToArray();
            Array.Sort(chosen, StringComparer.Ordinal);
            return string.Join(' ', chosen);
        }

        private static string SqlText(string? content) => content is null ? "NULL" : $"'{content}'";

        private static bool ContainsToken(string? content, string token)
            => content is not null
                && content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);

        private static void Run(EmbeddedConnection connection, string sql, List<string> history)
        {
            try
            {
                Execute(connection, sql);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"statement failed: {sql}\nerror: {error.Message}\n{HistoryTail(history, 40)}",
                    error);
            }

            history.Add(sql);
        }

        private static void AssertMatchesModel(
            EmbeddedConnection connection,
            SortedDictionary<long, string?> model,
            string token,
            ulong seed,
            List<string> history,
            string connectionName)
        {
            var expected = model.Where(entry => ContainsToken(entry.Value, token)).Select(static entry => entry.Key).ToArray();
            var actual = QueryIntegers(connection, $"SELECT id FROM docs WHERE fts_match(content, '{token}') ORDER BY id");
            actual.Should().Equal(
                expected,
                $"FTS model mismatch on {connectionName} for token \"{token}\"; seed={seed}\n{HistoryTail(history, 40)}");
        }

        private static string HistoryTail(List<string> history, int count)
            => "history tail:\n" + string.Join("\n", history.Skip(Math.Max(0, history.Count - count)));
    }

    /// <summary>A tiny deterministic PRNG so the seeded fuzz sequence is stable across runtimes.</summary>
    private sealed class SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong NextUInt64()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int NextInt(int exclusiveMax) => (int)(NextUInt64() % (ulong)exclusiveMax);

        public bool NextBool() => (NextUInt64() & 1UL) == 1UL;
    }
}
