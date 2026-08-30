using AwesomeAssertions;
using Ahtola.Core;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedSearchVirtualTableTests
{
    [Test]
    public void BuiltInModulesAreStaticallyRegisteredAndCreateInMemoryTables()
    {
        ManagedVirtualTableModuleRegistry.Resolve("fts5").Name.Should().Be("fts5");
        ManagedVirtualTableModuleRegistry.Resolve("rtree").Name.Should().Be("rtree");
        ManagedVirtualTableModuleRegistry.Resolve("rtree_i32").Name.Should().Be("rtree_i32");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title, body);");
        Execute(connection, "CREATE VIRTUAL TABLE bounds USING rtree(id, min_x, max_x, min_y, max_y);");
        Execute(connection, "CREATE VIRTUAL TABLE integer_bounds USING rtree_i32(id, min_x, max_x);");

        ReadRows(connection, "SELECT * FROM documents;").Should().BeEmpty();
        ReadRows(connection, "SELECT * FROM bounds;").Should().BeEmpty();
        ReadRows(connection, "SELECT * FROM integer_bounds;").Should().BeEmpty();

        Execute(connection, "DROP TABLE documents;");
        Execute(connection, "DROP TABLE bounds;");
        Execute(connection, "DROP TABLE integer_bounds;");
    }

    [Test]
    public void BuiltInModulesUseSqlDmlAndPredicatePlans()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title, body);");
        Execute(connection, "CREATE VIRTUAL TABLE bounds USING rtree(id, min_x, max_x, min_y, max_y);");
        Execute(connection, "CREATE TABLE metadata(tag TEXT);");

        Execute(connection, "INSERT INTO documents(title, body) VALUES ('Orchid', 'Purple flower');");
        Execute(connection, "INSERT INTO documents(title, body) VALUES ('Rose', 'Red flower');");
        Execute(connection, "INSERT INTO bounds(id, min_x, max_x, min_y, max_y) VALUES (3, 0, 10, 0, 10);");
        Execute(connection, "INSERT INTO bounds(id, min_x, max_x, min_y, max_y) VALUES (5, 20, 30, 20, 30);");
        Execute(connection, "INSERT INTO metadata(tag) VALUES ('flora');");

        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'orchid';")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("Orchid"));
        ReadRows(connection, "SELECT id FROM bounds WHERE max_x >= 5 AND min_x <= 5;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(3));
        ReadRows(
                connection,
                "SELECT d.rowid, d.title, m.tag "
                + "FROM documents d JOIN metadata m ON m.tag = 'flora' "
                + "WHERE documents MATCH 'orchid';")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("Orchid"), SqlValue.Text("flora"));
        ReadRows(
                connection,
                "SELECT b.rowid, b.id "
                + "FROM bounds b JOIN metadata m ON m.tag = 'flora' "
                + "WHERE b.max_x >= 5 AND b.min_x <= 5;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(3), SqlValue.Integer(3));
        ReadRows(
                connection,
                "SELECT d.rowid, d._rowid_, d.oid FROM documents d WHERE d._rowid_ = 1;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1));

        Execute(connection, "UPDATE documents SET body = 'White flower' WHERE title = 'Orchid';");
        Execute(connection, "DELETE FROM bounds WHERE id = 5;");

        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'purple';").Should().BeEmpty();
        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'white';")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("Orchid"));
        ReadRows(connection, "SELECT id FROM bounds;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(3));
    }

    [Test]
    public void Fts5AdapterUpdatesAndFiltersThroughTheVirtualTableContract()
    {
        var table = ManagedVirtualTableModuleRegistry.Resolve("fts5").Create(
            new ManagedVirtualTableCreateContext("documents", ["title", "body"]));

        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(7),
            SqlValue.Text("Orchid"),
            SqlValue.Text("Purple flower"),
            SqlValue.Null,
            SqlValue.Null,
        ]).Should().Be(7);
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(9),
            SqlValue.Text("Rose"),
            SqlValue.Text("Red flower"),
            SqlValue.Null,
            SqlValue.Null,
        ]).Should().Be(9);

        var plan = table.BestIndex(
        [
            new ManagedVirtualTableConstraint(
                0,
                ManagedVirtualTableConstraintOperator.Match),
        ],
        []);

        plan.ConstraintUsages.Should().Equal(new ManagedVirtualTableConstraintUsage(1, Omit: true));
        var matches = ReadRows(table, plan, [SqlValue.Text("orchid OR rose")]);
        matches.Should().HaveCount(2);
        matches[0][..4].Should().Equal(
            SqlValue.Integer(7), SqlValue.Text("Orchid"), SqlValue.Text("Purple flower"), SqlValue.Integer(2));
        matches[0][4].AsReal().Should().BeLessThan(0);
        matches[1][..4].Should().Equal(
            SqlValue.Integer(9), SqlValue.Text("Rose"), SqlValue.Text("Red flower"), SqlValue.Integer(2));
        matches[1][4].AsReal().Should().BeLessThan(0);

        table.Update(
        [
            SqlValue.Integer(7),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
        ]).Should().BeNull();
        matches = ReadRows(table, plan, [SqlValue.Text("orchid OR rose")]);
        matches.Should().ContainSingle();
        matches[0][..4].Should().Equal(
            SqlValue.Integer(9), SqlValue.Text("Rose"), SqlValue.Text("Red flower"), SqlValue.Integer(2));
        matches[0][4].AsReal().Should().BeLessThan(0);

        table.Begin();
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(11),
            SqlValue.Text("Lily"),
            SqlValue.Text("White flower"),
            SqlValue.Null,
            SqlValue.Null,
        ]);
        table.Rollback();
        ReadRows(table, plan, [SqlValue.Text("lily")]).Should().BeEmpty();
    }

    [Test]
    public void Fts5OptionsRowidsRankingAndAuxiliariesMatchStockSqlite()
    {
        const string create = """
            CREATE VIRTUAL TABLE documents USING fts5(
                title UNINDEXED,
                body,
                tokenize='ascii',
                prefix='2 3',
                detail=full,
                columnsize=0
            );
            """;
        string[] setup =
        [
            create,
            "INSERT INTO documents(rowid, title, body) VALUES (10, 'Alpha metadata', 'alpha beta');",
            "INSERT INTO documents VALUES ('Second metadata', 'alpha alpha delta');",
            "INSERT INTO documents(title, body) VALUES ('Third metadata', 'omega');",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var stock = new MsData.SqliteConnection("Data Source=:memory:");
        stock.Open();
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(stock, sql);
        }

        const string prefixQuery = """
            SELECT rowid, title, body
            FROM documents
            WHERE documents MATCH 'alp*'
            ORDER BY rowid;
            """;
        ReadRowsAsStrings(managed, prefixQuery).Should().Equal(ReadRowsAsStrings(stock, prefixQuery));

        const string columnQuery = """
            SELECT rowid
            FROM documents
            WHERE body MATCH 'alpha'
            ORDER BY rank;
            """;
        ReadRowsAsStrings(managed, columnQuery).Should().Equal(ReadRowsAsStrings(stock, columnQuery));
        const string descendingRankQuery = """
            SELECT rowid
            FROM documents
            WHERE documents MATCH 'alpha'
            ORDER BY rank DESC;
            """;
        ReadRowsAsStrings(managed, descendingRankQuery).Should().Equal(ReadRowsAsStrings(stock, descendingRankQuery));
        ReadRows(managed, "SELECT rowid FROM documents WHERE title MATCH 'alpha';").Should().BeEmpty();

        const string auxiliaryQuery = """
            SELECT rowid,
                   highlight(documents, 1, '<b>', '</b>'),
                   snippet(documents, 1, '[', ']', '...', 10)
            FROM documents
            WHERE documents MATCH 'alpha'
            ORDER BY rowid;
            """;
        ReadRowsAsStrings(managed, auxiliaryQuery).Should().Equal(ReadRowsAsStrings(stock, auxiliaryQuery));

        const string unindexedAuxiliaryQuery = """
            SELECT highlight(documents, 0, '<b>', '</b>'),
                   snippet(documents, -1, '[', ']', '...', 10)
            FROM documents
            WHERE documents MATCH 'alpha'
            ORDER BY rowid;
            """;
        ReadRowsAsStrings(managed, unindexedAuxiliaryQuery)
            .Should().Equal(ReadRowsAsStrings(stock, unindexedAuxiliaryQuery));

        const string coercedAuxiliaryQuery = """
            SELECT highlight(documents, '1', '<b>', '</b>'),
                   snippet(documents, '-1', '[', ']', '...', '10'),
                   bm25(documents, '2', '1') < 0
            FROM documents
            WHERE documents MATCH 'alpha'
            ORDER BY rowid;
            """;
        ReadRowsAsStrings(managed, coercedAuxiliaryQuery)
            .Should().Equal(ReadRowsAsStrings(stock, coercedAuxiliaryQuery));

        var ranked = ReadRows(
            managed,
            "SELECT rank, bm25(documents), bm25(documents, 2.0, 1.0) "
            + "FROM documents WHERE documents MATCH 'alpha' ORDER BY rank;");
        ranked.Should().HaveCount(2);
        foreach (var row in ranked)
        {
            row[0].AsReal().Should().BeLessThan(0);
            row[1].AsReal().Should().Be(row[0].AsReal());
            row[2].AsReal().Should().BeLessThan(0);
        }
        using (var stockRankCommand = stock.CreateCommand())
        {
            stockRankCommand.CommandText =
                "SELECT rank FROM documents WHERE documents MATCH 'alpha' ORDER BY rowid;";
            using var stockRankReader = stockRankCommand.ExecuteReader();
            var managedRanks = ReadRows(
                managed,
                "SELECT rank FROM documents WHERE documents MATCH 'alpha' ORDER BY rowid;");
            for (var index = 0; index < managedRanks.Count; index++)
            {
                stockRankReader.Read().Should().BeTrue();
                managedRanks[index][0].AsReal().Should().BeApproximately(stockRankReader.GetDouble(0), 1e-12);
            }
            stockRankReader.Read().Should().BeFalse();
        }

        Execute(managed, "INSERT INTO documents(documents) VALUES('optimize');");
        Execute(managed, "INSERT INTO documents(documents) VALUES('rebuild');");
        ReadRowsAsStrings(managed, prefixQuery).Should().Equal(ReadRowsAsStrings(stock, prefixQuery));

        string[] rowIdSetup =
        [
            "CREATE VIRTUAL TABLE rowids USING fts5(body);",
            "INSERT INTO rowids(rowid, body) VALUES (10, 'discarded');",
            "DELETE FROM rowids WHERE rowid = 10;",
            "INSERT INTO rowids(body) VALUES ('reused');",
        ];
        foreach (var sql in rowIdSetup)
        {
            Execute(managed, sql);
            Execute(stock, sql);
        }
        Execute(managed, "UPDATE rowids SET rowid = '5' WHERE rowid = 1;");
        Execute(stock, "UPDATE rowids SET rowid = '5' WHERE rowid = 1;");
        Execute(managed, "INSERT INTO rowids(rowid, body) VALUES (6.0, 'real rowid');");
        Execute(stock, "INSERT INTO rowids(rowid, body) VALUES (6.0, 'real rowid');");
        ReadRowsAsStrings(managed, "SELECT rowid FROM rowids ORDER BY rowid;")
            .Should().Equal(ReadRowsAsStrings(stock, "SELECT rowid FROM rowids ORDER BY rowid;"));
        Action fractionalRowId = () => Execute(
            managed,
            "INSERT INTO rowids(rowid, body) VALUES (6.5, 'fractional');");
        fractionalRowId.Should().Throw<EmbeddedSqlException>().WithMessage("*integer*");

        Execute(managed, "INSERT INTO documents(rowid, title, body) VALUES (30, 'Phrase', 'alpha x beta alpha beta');");
        Execute(stock, "INSERT INTO documents(rowid, title, body) VALUES (30, 'Phrase', 'alpha x beta alpha beta');");
        const string phraseAuxiliaryQuery = """
            SELECT rowid, highlight(documents, 1, '[', ']'), snippet(documents, 1, '<', '>', '...', 10)
            FROM documents
            WHERE documents MATCH '"alpha beta"'
            ORDER BY rowid;
            """;
        ReadRowsAsStrings(managed, phraseAuxiliaryQuery)
            .Should().Equal(ReadRowsAsStrings(stock, phraseAuxiliaryQuery));

        Execute(managed, "CREATE VIRTUAL TABLE snippet_choice USING fts5(a, b);");
        Execute(stock, "CREATE VIRTUAL TABLE snippet_choice USING fts5(a, b);");
        Execute(managed, "INSERT INTO snippet_choice VALUES ('alpha one two beta', 'beta alpha');");
        Execute(stock, "INSERT INTO snippet_choice VALUES ('alpha one two beta', 'beta alpha');");
        const string snippetChoiceQuery = """
            SELECT snippet(snippet_choice, -1, '[', ']', '...', 2)
            FROM snippet_choice
            WHERE snippet_choice MATCH 'alpha beta';
            """;
        ReadRowsAsStrings(managed, snippetChoiceQuery)
            .Should().Equal(ReadRowsAsStrings(stock, snippetChoiceQuery));

        string[] prefixScoreSetup =
        [
            "CREATE VIRTUAL TABLE prefix_scores USING fts5(body);",
            "INSERT INTO prefix_scores VALUES ('apple'), ('apply'), ('apple apply');",
            "CREATE VIRTUAL TABLE detail_none USING fts5(a, b, detail=none);",
            "INSERT INTO detail_none VALUES ('shared', ''), ('', 'shared');",
        ];
        foreach (var sql in prefixScoreSetup)
        {
            Execute(managed, sql);
            Execute(stock, sql);
        }

        AssertScoresMatchStock(
            managed,
            stock,
            "SELECT bm25(prefix_scores) FROM prefix_scores WHERE prefix_scores MATCH 'app*' ORDER BY rowid;");
        AssertScoresMatchStock(
            managed,
            stock,
            "SELECT bm25(detail_none, 10, 1) FROM detail_none WHERE detail_none MATCH 'shared' ORDER BY rowid;");
    }

    [Test]
    public void Fts5ReviewedQueryAndContentSemanticsMatchStockSqlite()
    {
        string[] setup =
        [
            "CREATE VIRTUAL TABLE documents USING fts5(a, b);",
            "INSERT INTO documents(rowid, a, b) VALUES (1, 'one', 'one');",
            "INSERT INTO documents(rowid, a, b) VALUES (2, 'one two', 'two');",
            "INSERT INTO documents(rowid, a, b) VALUES (3, 'one three', 'three');",
            "INSERT INTO documents(rowid, a, b) VALUES (4, 'one two three', 'two three');",
            "INSERT INTO documents(rowid, a, b) VALUES (10, 'blob', x'636166C3A9206E6F6972');",
            "CREATE TABLE no_shadow(marker TEXT);",
            "INSERT INTO no_shadow VALUES ('x');",
            "CREATE TABLE shadows(documents TEXT);",
            "INSERT INTO shadows VALUES ('ordinary');",
            "CREATE VIRTUAL TABLE near_docs USING fts5(body);",
            """
            INSERT INTO near_docs(rowid, body) VALUES
                (1, 'one x two'),
                (2, 'one x x two'),
                (3, 'two x one'),
                (4, 'one two three'),
                (5, 'one two throw'),
                (6, 'one'),
                (7, 'three one two');
            """,
            "CREATE VIRTUAL TABLE phrase_docs USING fts5(body);",
            "INSERT INTO phrase_docs(rowid, body) VALUES (1, 'one two three'), (2, 'one two throw'), (3, 'one two thyme');",
            "CREATE VIRTUAL TABLE near_scores USING fts5(a, b);",
            """
            INSERT INTO near_scores(rowid, a, b) VALUES
                (1, 'one two three', ''),
                (2, '', 'one two three'),
                (3, 'one two x three', ''),
                (4, '', 'one two x three'),
                (5, 'filler', 'filler');
            """,
            "CREATE VIRTUAL TABLE near_edges USING fts5(body);",
            """
            INSERT INTO near_edges(rowid, body) VALUES
                (1, 'a b c'),
                (2, 'a b b'),
                (3, 'one two three filler filler one two'),
                (4, 'one two three filler filler filler filler'),
                (5, 'or not near'),
                (6, 'one three two'),
                (7, 'one two three'),
                (8, 'only two three'),
                (9, 'one twin three'),
                (10, 'on two three');
            """,
            "CREATE VIRTUAL TABLE near_detail_column USING fts5(body, detail=column);",
            "INSERT INTO near_detail_column VALUES ('one two three');",
            "CREATE VIRTUAL TABLE near_detail_none USING fts5(body, detail=none);",
            "INSERT INTO near_detail_none VALUES ('one two three');",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        using var stock = new MsData.SqliteConnection("Data Source=:memory:");
        stock.Open();
        foreach (var sql in setup)
        {
            Execute(managed, sql);
            Execute(stock, sql);
        }

        string[] oracleQueries =
        [
            "SELECT rowid FROM documents WHERE b MATCH 'a:one' ORDER BY rowid;",
            "SELECT rowid FROM documents WHERE b MATCH 'a:one OR two' ORDER BY rowid;",
            "SELECT rowid FROM documents WHERE documents MATCH 'one NOT two three' ORDER BY rowid;",
            """
            SELECT rowid, typeof(b), highlight(documents, 1, '[', ']'),
                   snippet(documents, 1, '<', '>', '...', 8)
            FROM documents
            WHERE documents MATCH 'café'
            ORDER BY rowid;
            """,
            "SELECT rowid FROM near_docs WHERE near_docs MATCH 'NEAR(one two,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_docs WHERE near_docs MATCH 'NEAR(one x two,0)' ORDER BY rowid;",
            "SELECT rowid FROM near_docs WHERE near_docs MATCH 'NEAR(one,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_docs WHERE near_docs MATCH 'NEAR(one one,0)' ORDER BY rowid;",
            """
            SELECT rowid, highlight(near_docs, 0, '[', ']')
            FROM near_docs
            WHERE near_docs MATCH 'NEAR("one two" three,1)'
            ORDER BY rowid;
            """,
            """
            SELECT rowid, highlight(near_docs, 0, '[', ']')
            FROM near_docs
            WHERE near_docs MATCH 'NEAR(three "one two",0)'
            ORDER BY rowid;
            """,
            """
            SELECT rowid, highlight(near_docs, 0, '[', ']')
            FROM near_docs
            WHERE near_docs MATCH 'NEAR(one tw*,1)'
            ORDER BY rowid;
            """,
            """
            SELECT rowid, highlight(near_docs, 0, '[', ']')
            FROM near_docs
            WHERE near_docs MATCH 'NEAR("one tw"* three,1)'
            ORDER BY rowid;
            """,
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(a c \"a b\",0)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(a c \"a b\",1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(one two, 1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(\"one\"\"two\" three,1)' ORDER BY rowid;",
            """
            SELECT rowid, highlight(near_edges, 0, '[', ']')
            FROM near_edges
            WHERE near_edges MATCH 'one + two'
            ORDER BY rowid;
            """,
            """
            SELECT rowid, highlight(near_edges, 0, '[', ']')
            FROM near_edges
            WHERE near_edges MATCH 'NEAR(one + tw* three,1)'
            ORDER BY rowid;
            """,
            """
            SELECT rowid, highlight(near_edges, 0, '[', ']')
            FROM near_edges
            WHERE near_edges MATCH 'NEAR(on* + two three,1)'
            ORDER BY rowid;
            """,
            "SELECT rowid FROM near_edges WHERE near_edges MATCH '\"!!!\" + one' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH '\"\" + one' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(\"!!!\" + one two,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'on* + \"\" + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'on* + \"!!!\" + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'on* + \"\"' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'one + tw* + \"\" + three' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'on* + \"\"* + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'on* + \"\" + \"\"* + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'on + \"\"* + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'one + \"\" + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH '\"\" + on* + two' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(on* + \"\" + two three,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(on* + \"\"* + two three,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(on* + \"\" + \"\"* + two three,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(one + \"\" + two three,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR(\"\" + on* + two three,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH '\"!!!\" one' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'one \"!!!\"' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH '\"!!!\" AND one' ORDER BY rowid;",
            """
            SELECT rowid, highlight(near_edges, 0, '[', ']')
            FROM near_edges
            WHERE near_edges MATCH 'NEAR(a b,1)'
            ORDER BY rowid;
            """,
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'or' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'NEAR' ORDER BY rowid;",
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'OR\U0001F4A9' ORDER BY rowid;",
            "SELECT rowid FROM near_detail_column WHERE near_detail_column MATCH 'NEAR(one,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_detail_column WHERE near_detail_column MATCH 'NEAR(on* + \"\",1)' ORDER BY rowid;",
            "SELECT rowid FROM near_detail_column WHERE near_detail_column MATCH 'NEAR(on + \"\"*,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_detail_none WHERE near_detail_none MATCH 'NEAR(one,1)' ORDER BY rowid;",
            "SELECT rowid FROM near_detail_none WHERE near_detail_none MATCH 'NEAR(on* + \"\",1)' ORDER BY rowid;",
            "SELECT rowid FROM near_detail_none WHERE near_detail_none MATCH 'NEAR(on + \"\"*,1)' ORDER BY rowid;",
            """
            SELECT rowid, highlight(phrase_docs, 0, '[', ']')
            FROM phrase_docs
            WHERE phrase_docs MATCH '"one two thr"*'
            ORDER BY rowid;
            """,
            """
            SELECT (SELECT bm25(documents) FROM no_shadow),
                   (SELECT highlight(documents, 0, '[', ']') FROM no_shadow),
                   (SELECT snippet(documents, 0, '[', ']', '...', 5) FROM no_shadow)
            FROM documents
            WHERE documents MATCH 'one'
            ORDER BY rowid;
            """,
        ];
        foreach (var sql in oracleQueries)
            ReadRowsAsStrings(managed, sql).Should().Equal(ReadRowsAsStrings(stock, sql), sql);

        foreach (var positionalQuery in new[]
        {
            "on* + \"\" + two",
            "NEAR(on* + \"\" + two,1)",
        })
        {
            var sql = $"SELECT rowid FROM near_detail_column WHERE near_detail_column MATCH '{positionalQuery}';";
            Action managedPositional = () => ReadRowsAsStrings(managed, sql);
            Action stockPositional = () => ReadRowsAsStrings(stock, sql);
            managedPositional.Should().Throw<EmbeddedSqlException>();
            stockPositional.Should().Throw<MsData.SqliteException>();
        }

        AssertScoresMatchStock(
            managed,
            stock,
            """
            SELECT bm25(near_scores)
            FROM near_scores
            WHERE near_scores MATCH 'NEAR("one two" three,1)'
            ORDER BY rowid;
            """);
        AssertScoresMatchStock(
            managed,
            stock,
            """
            SELECT bm25(near_scores, 10, 1)
            FROM near_scores
            WHERE near_scores MATCH 'NEAR("one two" three,1)'
            ORDER BY rowid;
            """);
        AssertScoresMatchStock(
            managed,
            stock,
            """
            SELECT bm25(near_docs)
            FROM near_docs
            WHERE near_docs MATCH 'NEAR(one one,0)'
            ORDER BY rowid;
            """);
        AssertScoresMatchStock(
            managed,
            stock,
            """
            SELECT bm25(near_edges)
            FROM near_edges
            WHERE near_edges MATCH 'NEAR("one two" three,0)'
            ORDER BY rowid;
            """);

        foreach (var invalidBareword in new[] { "foo.bar", "foo/bar", "foo,bar" })
        {
            var sql =
                $"SELECT rowid FROM near_docs WHERE near_docs MATCH '{invalidBareword}';";
            Action managedInvalid = () => ReadRowsAsStrings(managed, sql);
            Action stockInvalid = () => ReadRowsAsStrings(stock, sql);
            managedInvalid.Should().Throw<EmbeddedSqlException>();
            stockInvalid.Should().Throw<MsData.SqliteException>();
        }

        const string reservedConcatenation =
            "SELECT rowid FROM near_edges WHERE near_edges MATCH 'one + OR';";
        Action managedReserved = () => ReadRowsAsStrings(managed, reservedConcatenation);
        Action stockReserved = () => ReadRowsAsStrings(stock, reservedConcatenation);
        managedReserved.Should().Throw<EmbeddedSqlException>();
        stockReserved.Should().Throw<MsData.SqliteException>();

        foreach (var invalidNear in new[] { "NEAR()", "NEAR(,1)" })
        {
            var sql = $"SELECT rowid FROM near_edges WHERE near_edges MATCH '{invalidNear}';";
            Action managedInvalid = () => ReadRowsAsStrings(managed, sql);
            Action stockInvalid = () => ReadRowsAsStrings(stock, sql);
            managedInvalid.Should().Throw<EmbeddedSqlException>();
            stockInvalid.Should().Throw<MsData.SqliteException>();
        }

        foreach (var auxiliary in new[]
                 {
                     "bm25(documents)",
                     "highlight(documents, 0, '[', ']')",
                     "snippet(documents, 0, '[', ']', '...', 5)",
                 })
        {
            var sql = $"""
                SELECT (SELECT {auxiliary} FROM shadows)
                FROM documents
                WHERE documents MATCH 'one';
                """;
            Action managedShadow = () => ReadRowsAsStrings(managed, sql);
            Action stockShadow = () => ReadRowsAsStrings(stock, sql);
            managedShadow.Should().Throw<EmbeddedSqlException>();
            stockShadow.Should().Throw<MsData.SqliteException>();
        }

        const string legacyNear =
            "SELECT rowid FROM near_docs WHERE near_docs MATCH 'NEAR/1(one two)';";
        Action managedLegacyNear = () => ReadRowsAsStrings(managed, legacyNear);
        Action stockLegacyNear = () => ReadRowsAsStrings(stock, legacyNear);
        managedLegacyNear.Should().Throw<EmbeddedSqlException>();
        stockLegacyNear.Should().Throw<MsData.SqliteException>();
    }

    [Test]
    public void Fts5AuxiliariesBindThroughJoinsAndMayBeShadowed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title, body);");
        Execute(connection, "CREATE TABLE tags(tag TEXT);");
        Execute(connection, "INSERT INTO documents(rowid, title, body) VALUES (42, 'Orchid', 'purple orchid flower');");
        Execute(connection, "INSERT INTO tags VALUES ('flora');");

        ReadRows(
                connection,
                "SELECT highlight(documents, 1, '[', ']'), snippet(documents, -1, '<', '>', '...', 8) "
                + "FROM documents JOIN tags ON tag = 'flora' WHERE documents MATCH 'orchid';")
            .Should().ContainSingle()
            .Which.Should().Equal(
                SqlValue.Text("purple [orchid] flower"),
                SqlValue.Text("<Orchid>"));

        ReadRows(connection, "SELECT bm25(documents), highlight(documents, 1, '[', ']') FROM documents;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Real(0), SqlValue.Text("purple orchid flower"));

        connection.RegisterScalarFunction("highlight", 4, static _ => SqlValue.Text("shadowed"));
        ReadRows(
                connection,
                "SELECT highlight(documents, 1, '[', ']') FROM documents WHERE documents MATCH 'orchid';")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("shadowed"));
    }

    [Test]
    public void Fts5DeclarationsAndCommandsRejectUnsupportedPayloadsFailClosed()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        string[] declarations =
        [
            "CREATE VIRTUAL TABLE bad USING fts5(body, content='source');",
            "CREATE VIRTUAL TABLE bad USING fts5(body, content_rowid='id');",
            "CREATE VIRTUAL TABLE bad USING fts5(body, tokenize='porter unicode61');",
            "CREATE VIRTUAL TABLE bad USING fts5(body, tokenize='porter');",
            "CREATE VIRTUAL TABLE bad USING fts5(body, detail=offsets);",
            "CREATE VIRTUAL TABLE bad USING fts5(body, columnsize=2);",
            "CREATE VIRTUAL TABLE bad USING fts5(body, prefix='2 two');",
            "CREATE VIRTUAL TABLE bad USING fts5(body, unknown=1);",
            $"CREATE VIRTUAL TABLE bad USING fts5({string.Join(", ", Enumerable.Range(0, 33).Select(static index => $"c{index:D2}"))});",
            "CREATE VIRTUAL TABLE bad USING fts5(rowid);",
        ];
        foreach (var declaration in declarations)
        {
            Action create = () => Execute(connection, declaration);
            create.Should().Throw<EmbeddedSqlException>();
        }

        Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(body);");
        Execute(connection, "INSERT INTO documents VALUES ('alpha');");
        Action deleteAll = () => Execute(connection, "INSERT INTO documents(documents) VALUES('delete-all');");
        deleteAll.Should().Throw<EmbeddedSqlException>().WithMessage("*contentless or external content*");
        Action unknownCommand = () => Execute(connection, "INSERT INTO documents(documents) VALUES('merge=4');");
        unknownCommand.Should().Throw<EmbeddedSqlException>().WithMessage("*unsupported managed fts5 command*");
        Action duplicateRowId = () => Execute(
            connection,
            "INSERT INTO documents(rowid, body) VALUES (1, 'replacement');");
        duplicateRowId.Should().Throw<EmbeddedSqlException>().WithMessage("*rowid 1 already exists*");

        Execute(connection, "INSERT OR IGNORE INTO documents(rowid, body) VALUES (1, 'ignored');");
        ReadRows(connection, "SELECT body FROM documents WHERE rowid=1;")
            .Single().Single().Should().Be(SqlValue.Text("alpha"));
        Execute(connection, "INSERT OR REPLACE INTO documents(rowid, body) VALUES (1, 'replacement');");
        ReadRows(connection, "SELECT body FROM documents WHERE rowid=1;")
            .Single().Single().Should().Be(SqlValue.Text("replacement"));
    }

    [Test]
    public void RTreeAdaptersUpdateAndApplyRangePlansThroughTheVirtualTableContract()
    {
        var table = ManagedVirtualTableModuleRegistry.Resolve("rtree").Create(
            new ManagedVirtualTableCreateContext("bounds", ["id", "min_x", "max_x", "min_y", "max_y"]));
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(3),
            SqlValue.Integer(3),
            SqlValue.Real(0),
            SqlValue.Real(10),
            SqlValue.Real(0),
            SqlValue.Real(10),
        ]).Should().Be(3);
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(5),
            SqlValue.Integer(5),
            SqlValue.Real(20),
            SqlValue.Real(30),
            SqlValue.Real(20),
            SqlValue.Real(30),
        ]).Should().Be(5);

        var plan = table.BestIndex(
        [
            new ManagedVirtualTableConstraint(
                1,
                ManagedVirtualTableConstraintOperator.LessThanOrEqual),
        ],
        []);

        plan.ConstraintUsages.Should().Equal(new ManagedVirtualTableConstraintUsage(1, Omit: true));
        var matches = ReadRows(table, plan, [SqlValue.Real(15)]);
        matches.Should().ContainSingle();
        matches[0].Should().Equal(
            SqlValue.Integer(3),
            SqlValue.Integer(3),
            SqlValue.Real(0),
            SqlValue.Real(10),
            SqlValue.Real(0),
            SqlValue.Real(10));

        var integerTable = ManagedVirtualTableModuleRegistry.Resolve("rtree_i32").Create(
            new ManagedVirtualTableCreateContext("integer_bounds", ["id", "min_x", "max_x"]));
        Action insertFractionalCoordinate = () => integerTable.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(2),
            SqlValue.Integer(2),
            SqlValue.Real(1.5),
            SqlValue.Integer(2),
        ]);
        insertFractionalCoordinate.Should().NotThrow();

        Action insertOutOfRangeCoordinate = () => integerTable.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Integer((long)int.MaxValue + 1),
            SqlValue.Integer(2),
        ]);
        insertOutOfRangeCoordinate.Should().NotThrow();
    }

    [Test]
    public void ManagedFtsAndRTreePersistAcrossFileReopenAndVacuumInto()
    {
        var sourcePath = CreateDatabasePath("managed-vtab-source");
        var backupPath = CreateDatabasePath("managed-vtab-backup");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(sourcePath))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title, body);");
                Execute(connection, "CREATE VIRTUAL TABLE bounds USING rtree(id, min_x, max_x, min_y, max_y);");
                Execute(connection, "CREATE TABLE metadata(value INTEGER);");
                Execute(connection, "ALTER TABLE metadata RENAME TO renamed_metadata;");
                Execute(connection, "INSERT INTO documents(title, body) VALUES ('Orchid', 'Purple flower');");
                Execute(connection, "INSERT INTO bounds VALUES (7, 0, 10, 0, 10);");
                Execute(connection, $"VACUUM INTO '{EscapeSqlLiteral(backupPath)}';");
            }

            AssertPersistedSearchState(sourcePath);
            AssertPersistedSearchState(backupPath);
        }
        finally
        {
            DeleteDatabase(sourcePath);
            DeleteDatabase(backupPath);
        }
    }

    [Test]
    public void OptionRichFts5RankingAuxiliariesAndCommandsSurviveReopenAndSavepointRollback()
    {
        var path = CreateDatabasePath("managed-fts5-options");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE VIRTUAL TABLE documents USING fts5(
                        title,
                        body UNINDEXED,
                        tokenize='ascii',
                        prefix='2 3',
                        detail=full,
                        columnsize=0
                    );
                    """);
                Execute(connection, "INSERT INTO documents(rowid, title, body) VALUES (10, 'Orchid orchid', 'private'), (20, 'Orchid', 'private');");
                Execute(connection, "INSERT INTO documents(documents) VALUES ('optimize');");
                Execute(connection, "CREATE VIRTUAL TABLE extreme_rowids USING fts5(body);");
                Execute(connection, "INSERT INTO extreme_rowids(rowid, body) VALUES (9223372036854775807, 'maximum');");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRowsAsStrings(
                    connection,
                    """
                    SELECT rowid, rank < 0, bm25(documents) < 0,
                           highlight(documents, 0, '[', ']'),
                           snippet(documents, 0, '<b>', '</b>', '...', 8)
                    FROM documents
                    WHERE title MATCH 'orch*'
                    ORDER BY rank;
                    """)
                    .Should().Equal(
                        "10\u001F1\u001F1\u001F[Orchid] [orchid]\u001F<b>Orchid</b> <b>orchid</b>",
                        "20\u001F1\u001F1\u001F[Orchid]\u001F<b>Orchid</b>");

                Execute(connection, "SAVEPOINT fts_state;");
                Execute(connection, "UPDATE documents SET title = 'Rose' WHERE rowid = 10;");
                Execute(connection, "INSERT INTO documents(documents) VALUES ('rebuild');");
                ReadRowsAsStrings(connection, "SELECT rowid FROM documents WHERE documents MATCH 'orch*' ORDER BY rank;")
                    .Should().Equal("20");
                Execute(connection, "ROLLBACK TO fts_state;");
                Execute(connection, "RELEASE fts_state;");

                ReadRowsAsStrings(connection, "SELECT rowid FROM documents WHERE documents MATCH 'orch*' ORDER BY rank;")
                    .Should().Equal("10", "20");
                Execute(connection, "INSERT INTO documents(documents) VALUES ('rebuild');");
                Execute(connection, "INSERT INTO extreme_rowids(body) VALUES ('random fallback');");
                var extremeRowIds = ReadRows(connection, "SELECT rowid FROM extreme_rowids ORDER BY rowid;");
                extremeRowIds.Should().HaveCount(2);
                extremeRowIds.Select(static row => row[0].AsInteger())
                    .Should().Contain(long.MaxValue)
                    .And.OnlyContain(static rowId => rowId > 0);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRowsAsStrings(connection, "SELECT rowid FROM documents WHERE documents MATCH 'orch*' ORDER BY rank;")
                    .Should().Equal("10", "20");
                ReadRows(connection, "SELECT rowid FROM extreme_rowids;").Should().HaveCount(2);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ManagedVirtualTableDmlAndSchemaChangesRollBackAtTransactionAndSavepointBoundaries()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title, body);");
        Execute(connection, "CREATE VIRTUAL TABLE bounds USING rtree(id, min_x, max_x);");

        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO documents(title, body) VALUES ('Orchid', 'Purple flower');");
        Execute(connection, "INSERT INTO bounds VALUES (1, 0, 10);");
        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'orchid';").Should().ContainSingle();
        Execute(connection, "ROLLBACK;");
        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'orchid';").Should().BeEmpty();
        ReadRows(connection, "SELECT id FROM bounds;").Should().BeEmpty();

        Execute(connection, "SAVEPOINT vtab_state;");
        Execute(connection, "INSERT INTO documents(title, body) VALUES ('Rose', 'Red flower');");
        Execute(connection, "CREATE VIRTUAL TABLE transient_bounds USING rtree(id, min_x, max_x);");
        Execute(connection, "ALTER TABLE bounds RENAME TO renamed_bounds;");
        Execute(connection, "ROLLBACK TO vtab_state;");
        Execute(connection, "RELEASE vtab_state;");

        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'rose';").Should().BeEmpty();
        ReadRows(connection, "SELECT id FROM bounds;").Should().BeEmpty();
        Action readRenamed = () => ReadRows(connection, "SELECT id FROM renamed_bounds;");
        readRenamed.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table*");
        Action readCreated = () => ReadRows(connection, "SELECT id FROM transient_bounds;");
        readCreated.Should().Throw<EmbeddedSqlException>().WithMessage("*no such table*");
    }

    [Test]
    public void RenamingManagedVirtualTableRewritesDependentViews()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title);");
        Execute(connection, "INSERT INTO documents(title) VALUES ('Orchid');");
        Execute(connection, "CREATE VIEW document_titles AS SELECT title FROM documents;");

        Execute(connection, "ALTER TABLE documents RENAME TO renamed_documents;");

        ReadRows(connection, "SELECT title FROM document_titles;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("Orchid"));
    }

    [Test]
    public void ManagedVirtualTableStatementFailureRestoresThePriorModulePayload()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE VIRTUAL TABLE bounds USING rtree(id, min_x, max_x);");

        Action insert = () => Execute(
            connection,
            "INSERT INTO bounds VALUES (1, 0, 10), (2, 20, 10);");

        insert.Should().Throw<EmbeddedSqlException>().WithMessage("*rtree constraint failed*");
        ReadRows(connection, "SELECT id FROM bounds;").Should().BeEmpty();
    }

    [Test]
    public void ManagedFtsAndRTreeRejectUnsupportedAndMalformedPersistencePayloads()
    {
        var fts5 = ManagedVirtualTableModuleRegistry.Resolve("fts5");
        var rtree = ManagedVirtualTableModuleRegistry.Resolve("rtree");

        Action unsupportedFtsVersion = () => fts5.Create(
            new ManagedVirtualTableCreateContext("documents", ["title"]),
            new ManagedVirtualTablePersistencePayload(2, []));
        unsupportedFtsVersion.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*unsupported fts5*version 2");

        Action truncatedFts = () => fts5.Create(
            new ManagedVirtualTableCreateContext("documents", ["title"]),
            new ManagedVirtualTablePersistencePayload(1, []));
        truncatedFts.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*truncated managed virtual-table persistence payload*");

        Action unsupportedRtreeVersion = () => rtree.Create(
            new ManagedVirtualTableCreateContext("bounds", ["id", "min_x", "max_x"]),
            new ManagedVirtualTablePersistencePayload(3, []));
        unsupportedRtreeVersion.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*unsupported rtree*version 3");

        Action truncatedRtree = () => rtree.Create(
            new ManagedVirtualTableCreateContext("bounds", ["id", "min_x", "max_x"]),
            new ManagedVirtualTablePersistencePayload(1, []));
        truncatedRtree.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*truncated managed virtual-table persistence payload*");

        var table = fts5.Create(new ManagedVirtualTableCreateContext("documents", ["title"]));
        try
        {
            var declaration = new EmbeddedDatabase.VirtualTableDefinition(
                "documents",
                "fts5",
                ["title"],
                new ManagedVirtualTablePersistencePayload(1, []),
                table);
            var (_, emptyPayload) = ManagedVirtualTableSchemaSql.Parse(
                ManagedVirtualTableSchemaSql.Build(declaration));
            emptyPayload.Version.Should().Be(1);
            emptyPayload.Bytes.Length.Should().Be(0);
        }
        finally
        {
            table.Destroy();
        }
    }

    [Test]
    public void ManagedVirtualTableCatalogRejectsMalformedOpaquePayloadOnReopen()
    {
        var path = CreateDatabasePath("managed-vtab-malformed");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title);");
                Execute(connection, "INSERT INTO documents(title) VALUES ('Orchid');");
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path}"))
            {
                sqlite.Open();
                using var command = sqlite.CreateCommand();
                command.CommandText = """
                    PRAGMA writable_schema=ON;
                    UPDATE sqlite_schema
                    SET sql = 'CREATE VIRTUAL TABLE "documents" USING "fts5"(title) /*ahtola-managed-vtab:1:not-base64*/'
                    WHERE type = 'table' AND name = 'documents';
                    PRAGMA writable_schema=OFF;
                    """;
                command.ExecuteNonQuery();
            }

            Action reopen = () =>
            {
                using var database = EmbeddedDatabase.OpenFile(path);
            };
            reopen.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*persistence payload is not valid base64*");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ClassicSqliteCanEnumerateTheRootpageZeroVirtualTableDeclaration()
    {
        var path = CreateDatabasePath("managed-vtab-schema");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
                Execute(connection, "CREATE VIRTUAL TABLE documents USING fts5(title);");

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Mode=ReadOnly");
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText = """
                SELECT type, name, tbl_name, rootpage, sql
                FROM sqlite_schema
                WHERE name = 'documents';
                """;
            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetString(0).Should().Be("table");
            reader.GetString(1).Should().Be("documents");
            reader.GetString(2).Should().Be("documents");
            reader.GetInt64(3).Should().Be(0);
            reader.GetString(4).Should().Contain("CREATE VIRTUAL TABLE");
            reader.GetString(4).Should().Contain("ahtola-managed-vtab:1:");
            reader.Read().Should().BeFalse();
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(
        ManagedVirtualTable table,
        ManagedVirtualTablePlan plan,
        IReadOnlyList<SqlValue> arguments)
    {
        using var cursor = table.Open();
        _ = cursor.Filter(plan, arguments);
        var rows = new List<SqlValue[]>();
        while (!cursor.Eof)
        {
            var row = new SqlValue[table.Schema.Columns.Count + 1];
            row[0] = SqlValue.Integer(cursor.RowId);
            for (var index = 0; index < table.Schema.Columns.Count; index++)
                row[index + 1] = cursor.Column(index);
            rows.Add(row);
            cursor.Next();
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() != StatementStepResult.Done)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadRowsAsStrings(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql)
            .Select(row => string.Join('\u001F', row.Select(Format)))
            .ToArray();

    private static IReadOnlyList<string> ReadRowsAsStrings(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index)
                    ? "<NULL>"
                    : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join('\u001F', row));
        }

        return rows;
    }

    private static void AssertScoresMatchStock(
        EmbeddedConnection managed,
        MsData.SqliteConnection stock,
        string sql)
    {
        var managedRows = ReadRows(managed, sql);
        using var command = stock.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        foreach (var row in managedRows)
        {
            reader.Read().Should().BeTrue();
            row[0].AsReal().Should().BeApproximately(reader.GetDouble(0), 1e-12);
        }

        reader.Read().Should().BeFalse();
    }

    private static string Format(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "<NULL>",
            SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Real => value.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => Convert.ToHexString(value.AsBlob().Span),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private static void AssertPersistedSearchState(string path)
    {
        using var database = EmbeddedDatabase.OpenFile(path);
        using var connection = database.Connect();
        ReadRows(connection, "SELECT title FROM documents WHERE documents MATCH 'orchid';")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("Orchid"));
        ReadRows(connection, "SELECT id FROM bounds WHERE max_x >= 5 AND min_x <= 5;")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(7));
    }

    private static string CreateDatabasePath(string name)
        => Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.db");

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
