using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class FromSourcePlannerParityTests
{
    private const string RichIndexSetup =
        """
        CREATE TABLE items(
            id INTEGER PRIMARY KEY,
            name TEXT,
            rank INTEGER,
            active INTEGER
        );
        CREATE INDEX items_rich
            ON items(lower(name) COLLATE NOCASE DESC, rank ASC)
            WHERE active = 1;
        INSERT INTO items VALUES
            (1, 'alpha', 2, 1),
            (2, 'Beta', 1, 1),
            (3, 'ALPHA', 1, 1),
            (4, NULL, 0, 1),
            (5, 'ignored', 9, 0);
        CREATE TABLE constrained(
            id INTEGER PRIMARY KEY,
            code TEXT COLLATE NOCASE UNIQUE
        );
        INSERT INTO constrained VALUES(1,'beta'),(2,'Alpha');
        """;

    [Test]
    public void ForcedRichIndexesMatchSqliteAndExposeTruthfulPlans()
    {
        var queries = new[]
        {
            "SELECT id,name FROM items INDEXED BY items_rich WHERE active=1;",
            """
            SELECT id,name FROM items AS i INDEXED BY items_rich
            WHERE 1=i.active AND lower(i.name) COLLATE NOCASE='alpha';
            """,
            """
            SELECT id,code FROM constrained
            INDEXED BY sqlite_autoindex_constrained_1;
            """,
        };

        foreach (var query in queries)
            AssertMatchesSqlite(RichIndexSetup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, RichIndexSetup);

        ReadPlanDetail(
                connection,
                "EXPLAIN QUERY PLAN SELECT id FROM items INDEXED BY items_rich WHERE active=1;")
            .Should().Be("SCAN items USING INDEX items_rich");
        ReadPlanDetail(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM items AS i INDEXED BY items_rich
                WHERE i.active=1 AND lower(i.name) COLLATE NOCASE='alpha';
                """)
            .Should().Be("SEARCH i USING INDEX items_rich");
        ReadOpenReadTargets(
                connection,
                """
                EXPLAIN SELECT id FROM items INDEXED BY items_rich
                WHERE active=1 AND lower(name) COLLATE NOCASE='alpha';
                """)
            .Should().ContainSingle()
            .Which.Should().Be("items USING INDEX items_rich");
    }

    [Test]
    public void QuotedAliasesMatchingIndexDirectiveKeywordsMatchSqlite()
    {
        AssertMatchesSqlite(
            RichIndexSetup,
            """
            SELECT "indexed".id
            FROM items AS "indexed"
            WHERE "indexed".active = 1
            ORDER BY "indexed".id;
            """);
    }

    [Test]
    public void AutomaticOrderingPlanRequiresEveryTermCollationDirectionAndNullPlacement()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, RichIndexSetup);

        const string matching =
            """
            SELECT id FROM items
            WHERE active=1
            ORDER BY lower(name) COLLATE NOCASE DESC NULLS LAST,
                     rank ASC NULLS FIRST;
            """;
        AssertMatchesSqlite(RichIndexSetup, matching);
        ReadPlanDetail(connection, "EXPLAIN QUERY PLAN " + matching)
            .Should().Be("SCAN items USING INDEX items_rich");

        ReadPlanDetail(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM items
                WHERE active=1
                ORDER BY lower(name) COLLATE NOCASE DESC, rank DESC;
                """)
            .Should().NotContain("USING INDEX items_rich");
        ReadPlanDetail(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM items
                WHERE active=1
                ORDER BY lower(name) COLLATE NOCASE DESC NULLS FIRST, rank ASC;
                """)
            .Should().NotContain("USING INDEX items_rich");
        ReadPlanDetail(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id FROM items
                WHERE active=1
                ORDER BY lower(name) COLLATE BINARY DESC, rank ASC;
                """)
            .Should().NotContain("USING INDEX items_rich");
    }

    [Test]
    public void MissingAndUnsafeForcedIndexesMatchSqliteErrors()
    {
        const string setup =
            """
            CREATE TABLE left_items(id INTEGER PRIMARY KEY, active INTEGER);
            CREATE TABLE right_items(id INTEGER PRIMARY KEY, active INTEGER);
            CREATE TABLE third_items(value INTEGER);
            CREATE INDEX left_active ON left_items(id) WHERE active=1;
            CREATE INDEX right_null ON right_items(id) WHERE active IS NULL;
            INSERT INTO left_items VALUES(1,1),(2,0);
            INSERT INTO right_items VALUES(1,5),(2,NULL);
            INSERT INTO third_items VALUES(1);
            CREATE VIEW item_view AS SELECT * FROM left_items;
            """;
        var cases = new[]
        {
            ("SELECT * FROM left_items INDEXED BY missing;", "no such index: missing"),
            ("SELECT * FROM left_items INDEXED BY right_null;", "no such index: right_null"),
            ("SELECT * FROM left_items INDEXED BY left_active;", "no query solution"),
            (
                """
                SELECT left_items.id
                FROM left_items INDEXED BY left_active
                LEFT JOIN right_items
                  ON right_items.id=left_items.id AND left_items.active=1;
                """,
                "no query solution"),
            (
                """
                SELECT left_items.id
                FROM left_items
                LEFT JOIN right_items INDEXED BY right_null
                  ON right_items.id=left_items.id
                WHERE right_items.active IS NULL;
                """,
                "no query solution"),
            (
                """
                SELECT left_items.id
                FROM left_items
                LEFT JOIN right_items INDEXED BY right_null
                  ON right_items.id=left_items.id
                JOIN third_items ON right_items.active IS NULL;
                """,
                "no query solution"),
            ("SELECT * FROM item_view INDEXED BY left_active;", "no such index: left_active"),
            (
                "WITH c AS (SELECT * FROM left_items) SELECT * FROM c INDEXED BY left_active;",
                "no such index"),
        };

        foreach (var (query, expected) in cases)
        {
            CaptureManagedError(setup, query).Should().Contain(expected);
            CaptureSqliteError(setup, query).Should().Contain(expected);
        }

        AssertMatchesSqlite(setup, "SELECT * FROM item_view NOT INDEXED ORDER BY id;");
    }

    [Test]
    public void JoinHintsUseOnlyPredicatesThatAreSafeForEachOuterJoinSide()
    {
        const string setup =
            """
            CREATE TABLE left_items(id INTEGER PRIMARY KEY, active INTEGER);
            CREATE TABLE right_items(id INTEGER PRIMARY KEY, active INTEGER);
            CREATE INDEX left_active ON left_items(id DESC) WHERE active=1;
            CREATE INDEX right_null ON right_items(id DESC) WHERE active IS NULL;
            CREATE INDEX right_active ON right_items(id DESC) WHERE active=1;
            INSERT INTO left_items VALUES(1,1),(2,0),(3,1);
            INSERT INTO right_items VALUES(1,NULL),(2,1),(3,1);
            """;
        var queries = new[]
        {
            """
            SELECT l.id,r.id
            FROM left_items AS l INDEXED BY left_active
            JOIN right_items AS r NOT INDEXED ON r.id=l.id
            WHERE l.active=1
            ORDER BY l.id;
            """,
            """
            SELECT l.id,r.id
            FROM left_items AS l
            LEFT JOIN right_items AS r INDEXED BY right_null
              ON r.id=l.id AND r.active IS NULL
            ORDER BY l.id;
            """,
            """
            SELECT l.id,r.id
            FROM left_items AS l
            LEFT JOIN right_items AS r INDEXED BY right_active ON r.id=l.id
            WHERE r.active=1
            ORDER BY l.id;
            """,
        };

        foreach (var query in queries)
            AssertMatchesSqlite(setup, query);

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        ReadPlanDetail(connection, "EXPLAIN QUERY PLAN " + queries[0])
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        using var explain = connection.Prepare("EXPLAIN " + queries[0]);
        Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
            .Message.Should().Be(
                "EXPLAIN is only supported for statements lowered to the bytecode compiler.");
    }

    [Test]
    public void NotIndexedUsesTheTableScanAndForcedCallbacksKeepIndexOrder()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            """
            CREATE TABLE entries(id INTEGER PRIMARY KEY, name TEXT);
            CREATE INDEX entries_name ON entries(name DESC);
            INSERT INTO entries VALUES(1,'alpha'),(2,'gamma'),(3,'beta');
            """);

        ReadPlanDetail(
                connection,
                "EXPLAIN QUERY PLAN SELECT id FROM entries NOT INDEXED WHERE id>0;")
            .Should().Be("MANAGED EVALUATOR FALLBACK");
        ReadOpenReadTargets(
                connection,
                "EXPLAIN SELECT id FROM entries NOT INDEXED WHERE id>0;")
            .Should().ContainSingle()
            .Which.Should().Be("entries");

        var calls = new List<long>();
        connection.RegisterScalarFunction(
            "observe",
            1,
            values =>
            {
                calls.Add(values[0].AsInteger());
                return values[0];
            });

        ReadPlanDetail(
                connection,
                "EXPLAIN QUERY PLAN SELECT observe(id) FROM entries INDEXED BY entries_name;")
            .Should().Be("SCAN entries USING INDEX entries_name");
        calls.Should().BeEmpty();

        using (var explain = connection.Prepare(
                   "EXPLAIN SELECT observe(id) FROM entries INDEXED BY entries_name;"))
        {
            Assert.Throws<EmbeddedSqlException>(() => explain.Step())!
                .Message.Should().Contain("evaluator-managed index plan");
        }
        calls.Should().BeEmpty();

        Query(connection, "SELECT observe(id) FROM entries INDEXED BY entries_name;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(2, 3, 1);
        calls.Should().Equal(2, 3, 1);

        calls.Clear();
        using var canceled = connection.Prepare(
            "SELECT observe(id) FROM entries INDEXED BY entries_name;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => canceled.Step(cancellation.Token));
        calls.Should().BeEmpty();

        Action lateSubqueryError = () => Query(
            connection,
            """
            SELECT observe(id),
                   (SELECT id FROM entries INDEXED BY missing WHERE id=1)
            FROM entries
            LIMIT 1;
            """);
        lateSubqueryError.Should().Throw<EmbeddedSqlException>()
            .WithMessage("no such index: missing");
        calls.Should().BeEmpty();
    }

    [Test]
    public void HintsSurviveWithoutRowidTempCtasForeignKeysAndAttachRouting()
    {
        var mainPath = CreateDatabasePath("main");
        var auxiliaryPath = CreateDatabasePath("aux");
        try
        {
            using (var auxiliary = EmbeddedDatabase.OpenFile(auxiliaryPath))
            using (var connection = auxiliary.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE ranked(
                        code TEXT PRIMARY KEY DESC,
                        active INTEGER
                    ) WITHOUT ROWID;
                    CREATE INDEX ranked_active
                        ON ranked(active DESC, code COLLATE NOCASE ASC)
                        WHERE active=1;
                    INSERT INTO ranked VALUES('beta',1),('Alpha',1),('ignored',0);
                    """);
            }

            IReadOnlyList<string> managedAttachedOrder;
            using (var main = EmbeddedDatabase.OpenFile(mainPath))
            using (var connection = main.Connect())
            {
                Execute(
                    connection,
                    $"""
                    PRAGMA foreign_keys=ON;
                    CREATE TABLE parent(id INTEGER PRIMARY KEY);
                    CREATE TABLE child(
                        id INTEGER PRIMARY KEY,
                        parent_id INTEGER REFERENCES parent(id),
                        label TEXT
                    );
                    CREATE INDEX child_label
                        ON child(label COLLATE NOCASE DESC)
                        WHERE parent_id=1;
                    INSERT INTO parent VALUES(1);
                    INSERT INTO child VALUES(1,1,'alpha'),(2,1,'Beta');
                    CREATE TEMP TABLE staged AS
                        SELECT id,label FROM child INDEXED BY child_label WHERE parent_id=1;
                    CREATE INDEX temp.staged_label ON staged(label DESC);
                    ATTACH DATABASE '{EscapeSqlLiteral(auxiliaryPath)}' AS aux;
                    """);

                Query(connection, "SELECT id FROM temp.staged INDEXED BY staged_label;")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(1, 2);
                managedAttachedOrder = Query(
                        connection,
                        """
                        SELECT code FROM aux.ranked INDEXED BY ranked_active
                        WHERE active=1;
                        """)
                    .Select(row => row[0].AsText())
                    .ToArray();
                Action foreignKeyViolation = () =>
                    Execute(connection, "INSERT INTO child VALUES(3,99,'invalid');");
                foreignKeyViolation.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*FOREIGN KEY constraint failed*");
                Execute(connection, "DETACH aux;");
            }

            using var sqlite = new MsData.SqliteConnection(
                $"Data Source={auxiliaryPath};Pooling=False");
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText =
                "SELECT code FROM ranked INDEXED BY ranked_active WHERE active=1;";
            var sqliteOrder = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                sqliteOrder.Add(reader.GetString(0));
            managedAttachedOrder.Should().Equal(sqliteOrder);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(mainPath);
            DeleteDatabase(auxiliaryPath);
        }
    }

    [Test]
    public void OnlyExecutableTableValuedSourcesReachTheAstAndCtasBoundary()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Query(
                connection,
                "SELECT series.value FROM generate_series(1,3,1) AS series;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2, 3);

        Query(connection, "SELECT value FROM json_each('[1,2]');")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2);

        Action unsupportedQuery = () =>
            connection.Prepare("SELECT * FROM fts5vocab('t','row');");
        unsupportedQuery.Should().Throw<EmbeddedSqlException>().WithMessage(
            "Managed table-valued source 'fts5vocab' is not supported: "
            + "no module registration, planner, or execution contract is available.*");

        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;");
        Action unsupportedCtas = () =>
            connection.Prepare("CREATE TEMP TABLE leaked AS SELECT * FROM missing_module(1);");
        unsupportedCtas.Should().Throw<EmbeddedSqlException>()
            .WithMessage("Managed table-valued source 'missing_module' is not supported:*");
        ReadScalar(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);
        Action leaked = () => connection.Prepare("SELECT * FROM leaked;").GetColumnCount();
        leaked.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: leaked");
    }

    private static void AssertMatchesSqlite(string setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var index = 0; index < sqlite.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index]);
    }

    private static QueryOutput RunManaged(string setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        using var statement = connection.Prepare(query);
        var columns = Enumerable.Range(0, statement.GetColumnCount())
            .Select(statement.GetColumnName)
            .ToArray();
        var rows = new List<string[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(index => Normalize(statement.GetValue(index)))
                .ToArray());
        }

        return new QueryOutput(columns, rows);
    }

    private static QueryOutput RunSqlite(string setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = setup;
            setupCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => Normalize(reader.GetValue(index)))
                .ToArray());
        }

        return new QueryOutput(columns, rows);
    }

    private static string CaptureManagedError(string setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, setup);
        var error = Assert.Throws<EmbeddedSqlException>(() => Execute(connection, query));
        return error!.Message;
    }

    private static string CaptureSqliteError(string setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = setup;
            setupCommand.ExecuteNonQuery();
        }

        var error = Assert.Throws<MsData.SqliteException>(() =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            command.ExecuteNonQuery();
        });
        return error!.Message;
    }

    private static string ReadPlanDetail(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var detail = statement.GetValue(3).AsText();
        statement.Step().Should().Be(StatementStepResult.Done);
        return detail;
    }

    private static IReadOnlyList<string> ReadOpenReadTargets(
        EmbeddedConnection connection,
        string sql)
    {
        using var statement = connection.Prepare(sql);
        var targets = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            if (statement.GetValue(1).AsText() == "OpenReadCursor")
                targets.Add(statement.GetValue(5).AsText());
        }

        return targets;
    }

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Single()[0];

    private static IReadOnlyList<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(statement.GetValue)
                .ToArray());
        }

        return rows;
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step() == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static string Normalize(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => "N:",
            SqlValueKind.Integer => $"I:{value.AsInteger()}",
            SqlValueKind.Real => $"R:{value.AsReal():R}",
            SqlValueKind.Text => $"T:{value.AsText()}",
            SqlValueKind.Blob => $"B:{Convert.ToBase64String(value.AsBlob().Span)}",
            _ => throw new InvalidOperationException($"Unknown managed value kind {value.Kind}."),
        };

    private static string Normalize(object value)
        => value switch
        {
            DBNull => "N:",
            long integer => $"I:{integer}",
            double real => $"R:{real:R}",
            string text => $"T:{text}",
            byte[] blob => $"B:{Convert.ToBase64String(blob)}",
            _ => throw new InvalidOperationException($"Unknown SQLite value type {value.GetType().Name}."),
        };

    private static string CreateDatabasePath(string label)
        => Path.Combine(Path.GetTempPath(), $"Ahtola-source-{label}-{Guid.NewGuid():N}.db");

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-journal", path + "-shm" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<string[]> Rows);
}
