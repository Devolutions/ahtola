using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedConstraintSemanticsTests
{
    [Test]
    public void CheckAndTableUniqueConstraintsMatchSqliteAndKeepStatementsAtomic()
    {
        string[] setup =
        [
            """
            CREATE TABLE items(
                id INTEGER PRIMARY KEY,
                quantity INTEGER CHECK (quantity > 0),
                limit_value INTEGER,
                CONSTRAINT within_limit CHECK (quantity <= limit_value),
                CONSTRAINT item_quantity UNIQUE (id, quantity)
            );
            """,
            "INSERT INTO items VALUES (1, 2, 5), (2, 3, 6);",
        ];

        AssertErrorMatchesSqlite(setup, "INSERT INTO items VALUES (3, 1, 5), (4, -1, 5);");
        AssertQueryMatchesSqlite(setup, "SELECT id, quantity, limit_value FROM items ORDER BY id;");

        AssertErrorMatchesSqlite(setup, "UPDATE items SET quantity = limit_value + 1;");
        AssertQueryMatchesSqlite(setup, "SELECT id, quantity, limit_value FROM items ORDER BY id;");

        AssertErrorMatchesSqlite(setup, "INSERT INTO items VALUES (1, 2, 9);");
    }

    [Test]
    public void NullChecksAndConstraintConflictClausesMatchSqlite()
    {
        AssertQueryMatchesSqlite(
            [
                """
                CREATE TABLE entries(
                    id INTEGER PRIMARY KEY,
                    code TEXT UNIQUE ON CONFLICT IGNORE,
                    required INTEGER NOT NULL ON CONFLICT REPLACE DEFAULT (2 + 3),
                    value INTEGER CHECK (value > 0)
                );
                """,
                "INSERT INTO entries VALUES (1, 'a', NULL, NULL);",
                "INSERT INTO entries VALUES (2, 'a', 9, 1);",
                "INSERT OR IGNORE INTO entries VALUES (3, 'b', 9, -1);",
            ],
            "SELECT id, code, required, value FROM entries ORDER BY id;");
    }

    [Test]
    public void ExpressionDefaultsDeclaredTypesChecksAndTableUniqueRoundTripThroughFileCatalog()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE metrics(
                        id INTEGER PRIMARY KEY,
                        amount DOUBLE PRECISION DEFAULT (abs(-4) + 1),
                        label CHARACTER VARYING(20) DEFAULT (upper('x')),
                        created TEXT DEFAULT CURRENT_TIMESTAMP,
                        CONSTRAINT positive CHECK (amount > 0),
                        CONSTRAINT metric_value UNIQUE (label, amount) ON CONFLICT IGNORE
                    );
                    """);
                Execute(connection, "INSERT INTO metrics(id) VALUES (1);");
                Execute(connection, "INSERT INTO metrics(id) VALUES (2);");
                ScalarInteger(connection, "SELECT COUNT(*) FROM metrics;").Should().Be(1);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                var tableInfo = ReadRows(connection, "PRAGMA table_info(metrics);");
                tableInfo[1][2].Should().Be(SqlValue.Text("DOUBLE PRECISION"));
                tableInfo[1][4].Should().Be(SqlValue.Text("abs(-4) + 1"));
                tableInfo[2][2].Should().Be(SqlValue.Text("CHARACTER VARYING(20)"));

                Execute(connection, "INSERT INTO metrics(id, label) VALUES (2, 'Y');");
                Action invalidUpdate = () => Execute(connection, "UPDATE metrics SET amount = -1;");
                invalidUpdate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: positive");

                ReadRows(connection, "SELECT id, amount, label, typeof(amount) FROM metrics ORDER BY id;")
                    .Should().BeEquivalentTo(
                    [
                        new[] { SqlValue.Integer(1), SqlValue.Real(5), SqlValue.Text("X"), SqlValue.Text("real") },
                        new[] { SqlValue.Integer(2), SqlValue.Real(5), SqlValue.Text("Y"), SqlValue.Text("real") },
                    ],
                    options => options.WithStrictOrdering());
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarInteger(
                sqlite,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_autoindex_metrics_1' AND sql IS NULL;")
                .Should().Be(1);
            var schemaSql = ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'metrics';");
            schemaSql.Should().Contain("DOUBLE PRECISION")
                .And.Contain("CHARACTER VARYING(20)")
                .And.Contain("DEFAULT (abs(-4) + 1)")
                .And.Contain("CONSTRAINT positive CHECK (amount > 0)")
                .And.Contain("UNIQUE (label, amount) ON CONFLICT IGNORE");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ExplicitNullAndDateTimeDefaultsMatchSqlite()
    {
        AssertQueryMatchesSqlite(
            [
                "CREATE TABLE values_table(a TEXT NULL, b NULL, created TEXT DEFAULT (datetime('now')));",
                "INSERT INTO values_table(a) VALUES ('x');",
            ],
            "SELECT type, name, tbl_name FROM sqlite_master WHERE type = 'table';");

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE values_table(a TEXT NULL, b NULL, created TEXT DEFAULT (datetime('now')));");
        Execute(connection, "INSERT INTO values_table(a) VALUES ('x');");
        var info = ReadRows(connection, "PRAGMA table_info(values_table);");
        info[0][2].Should().Be(SqlValue.Text("TEXT"));
        info[1][2].Should().Be(SqlValue.Text(string.Empty));
        ScalarInteger(connection, "SELECT created IS NOT NULL FROM values_table;").Should().Be(1);
    }

    [Test]
    public void UnknownFunctionsInDefaultsAndChecksDeferErrorsToEvaluation()
    {
        // SQLite resolves functions in CHECK constraints at CREATE time but defers DEFAULT
        // expressions to insert time, where unknown names surface as evaluation errors.
        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Execute(sqlite, "CREATE TABLE deferred_default(id INTEGER, stamp TEXT DEFAULT (nosuchfunc(1)));");
            Action sqliteDefault = () => Execute(sqlite, "INSERT INTO deferred_default DEFAULT VALUES;");
            sqliteDefault.Should().Throw<MsData.SqliteException>().WithMessage("*unknown function*");

            Action sqliteCheck = () => Execute(sqlite, "CREATE TABLE rejected_check(id INTEGER CHECK (nosuchcheck(id) > 0));");
            sqliteCheck.Should().Throw<MsData.SqliteException>().WithMessage("*no such function*");
        }

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE deferred_default(id INTEGER, stamp TEXT DEFAULT (nosuchfunc(1)));");
                Action managedDefault = () => Execute(connection, "INSERT INTO deferred_default DEFAULT VALUES;");
                managedDefault.Should().Throw<EmbeddedSqlException>().WithMessage("*no such function*");

                Action managedCheck = () => Execute(connection, "CREATE TABLE rejected_check(id INTEGER CHECK (nosuchcheck(id) > 0));");
                managedCheck.Should().Throw<EmbeddedSqlException>().WithMessage("*no such function*");

                // Non-deterministic built-ins are valid in CHECK constraints.
                Execute(connection, "CREATE TABLE nondet_check(id INTEGER CHECK (random() IS NOT NULL));");
                Execute(connection, "INSERT INTO nondet_check VALUES (1);");
                ScalarInteger(connection, "SELECT count(*) FROM nondet_check;").Should().Be(1);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadRows(connection, "PRAGMA table_info(deferred_default);").Should().HaveCount(2);
                ReadRows(connection, "PRAGMA table_info(nondet_check);").Should().HaveCount(1);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'deferred_default';")
                .Should().Contain("DEFAULT (nosuchfunc(1))");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void DefaultExpressionsAreNotEvaluatedForColumnsTheInsertProvides()
    {
        // SQLite evaluates a DEFAULT expression only when the column is omitted, so an
        // unknown function in one must not fail inserts that supply the value explicitly.
        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Execute(sqlite, "CREATE TABLE t(id INTEGER, stamp TEXT DEFAULT (nosuchfunc(1)));");
            Execute(sqlite, "INSERT INTO t(id, stamp) VALUES (1, 'explicit');");
            ScalarText(sqlite, "SELECT stamp FROM t;").Should().Be("explicit");

            Action omitted = () => Execute(sqlite, "INSERT INTO t(id) VALUES (2);");
            omitted.Should().Throw<MsData.SqliteException>().WithMessage("*unknown function*");
        }

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER, stamp TEXT DEFAULT (nosuchfunc(1)));");
        Execute(connection, "INSERT INTO t(id, stamp) VALUES (1, 'explicit');");
        ScalarText(connection, "SELECT stamp FROM t;").Should().Be("explicit");

        Action managedOmitted = () => Execute(connection, "INSERT INTO t(id) VALUES (2);");
        managedOmitted.Should().Throw<EmbeddedSqlException>().WithMessage("*no such function*");
    }

    [Test]
    public void NonDeterministicBuiltInDefaultsEvaluatePerRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE values_table(id INTEGER, stamp TEXT DEFAULT (strftime('%Y', 'now')), payload BLOB DEFAULT (randomblob(4)));");
        Execute(connection, "INSERT INTO values_table(id) VALUES (1);");

        var row = ReadRows(connection, "SELECT length(stamp), typeof(payload), length(payload) FROM values_table;").Single();
        row.Should().Equal(SqlValue.Integer(4), SqlValue.Text("blob"), SqlValue.Integer(4));
    }

    [Test]
    public void AggregateFunctionsAreRejectedInChecksButDeferredInDefaults()
    {
        // SQLite rejects aggregates in CHECK constraints at CREATE ("misuse of aggregate
        // function") but accepts them in DEFAULT expressions, failing only at insert time.
        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Action sqliteCheck = () => Execute(sqlite, "CREATE TABLE t(x INTEGER CHECK (sum(x) > 0));");
            sqliteCheck.Should().Throw<MsData.SqliteException>().WithMessage("*aggregate*");

            Execute(sqlite, "CREATE TABLE d(x INTEGER DEFAULT (count(*)));");
            Action sqliteDefault = () => Execute(sqlite, "INSERT INTO d DEFAULT VALUES;");
            sqliteDefault.Should().Throw<MsData.SqliteException>();
        }

        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Action managedCheck = () => Execute(connection, "CREATE TABLE t(x INTEGER CHECK (sum(x) > 0));");
        managedCheck.Should().Throw<EmbeddedSqlException>()
            .WithMessage("misuse of aggregate function SUM()");

        Execute(connection, "CREATE TABLE d(x INTEGER DEFAULT (count(*)));");
        Action managedDefault = () => Execute(connection, "INSERT INTO d DEFAULT VALUES;");
        managedDefault.Should().Throw<EmbeddedSqlException>();
    }

    [Test]
    public void AlterAddColumnValidatesChecksAndAcceptsSignedLiteralDefaults()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(id INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1);");

        Action invalid = () => Execute(
            connection,
            "ALTER TABLE values_table ADD COLUMN invalid INTEGER DEFAULT -1 CHECK (invalid > 0);");
        invalid.Should().Throw<EmbeddedSqlException>().WithMessage("CHECK constraint failed: invalid > 0");

        Execute(connection, "ALTER TABLE values_table ADD COLUMN valid INTEGER DEFAULT -1;");
        ScalarInteger(connection, "SELECT valid FROM values_table;").Should().Be(-1);
    }

    [Test]
    public void UniqueUpdatesUseSqliteRowwiseConflictOrder()
    {
        string[] setup =
        [
            "CREATE TABLE values_table(value INTEGER UNIQUE);",
            "INSERT INTO values_table VALUES (1), (2);",
        ];

        using var database = new EmbeddedDatabase();
        using var managed = database.Connect();
        foreach (var sql in setup)
            Execute(managed, sql);
        Action managedSwap = () => Execute(managed, "UPDATE values_table SET value = 3 - value;");
        managedSwap.Should().Throw<EmbeddedSqlException>()
            .WithMessage("UNIQUE constraint failed: values_table.value");
        ReadRows(managed, "SELECT value FROM values_table ORDER BY value;")
            .Select(row => row[0].AsInteger())
            .Should().Equal(1, 2);

        Execute(managed, "CREATE TABLE configured(value INTEGER UNIQUE ON CONFLICT IGNORE);");
        Execute(managed, "INSERT INTO configured VALUES (1), (2);");
        Execute(managed, "UPDATE configured SET value = 1;");
        ScalarInteger(managed, "SELECT COUNT(*) FROM configured;").Should().Be(2);
    }

    [Test]
    public void RenameRewritesConstraintExpressionsThatReferenceTheRenamedColumn()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE values_table(a INTEGER, b INTEGER, UNIQUE(a, b), CHECK(a < b));");
        Execute(connection, "ALTER TABLE values_table RENAME COLUMN a TO first;");

        // SQLite rewrites the stored CHECK expression and the table-level UNIQUE key in place.
        var schema = ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name='values_table';")
            .Single()[0].AsText();
        schema.Should().Contain("CHECK(first < b)").And.Contain("UNIQUE(first, b)")
            .And.NotContain("(a ").And.NotContain("\"a\"");

        Execute(connection, "INSERT INTO values_table VALUES (1, 2);");
        Action violation = () => Execute(connection, "INSERT INTO values_table VALUES (5, 2);");
        violation.Should().Throw<EmbeddedSqlException>().WithMessage("*CHECK*");
        ReadRows(connection, "SELECT first, b FROM values_table;").Single()
            .Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void EmptyTableAddColumnRejectsInvalidSchemaBeforeCatalogPublication()
    {
        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Execute(sqlite, "CREATE TABLE values_table(id INTEGER);");
            Action invalidCheck = () => Execute(
                sqlite,
                "ALTER TABLE values_table ADD COLUMN checked INTEGER CHECK(missing > 0);");
            invalidCheck.Should().Throw<MsData.SqliteException>().WithMessage("*no such column: missing*");
            Action invalidDefault = () => Execute(
                sqlite,
                "ALTER TABLE values_table ADD COLUMN defaulted INTEGER DEFAULT (missing);");
            invalidDefault.Should().Throw<MsData.SqliteException>()
                .WithMessage("*default value of column [defaulted] is not constant*");
        }

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE values_table(id INTEGER);");

                Action invalidCheck = () => Execute(
                    connection,
                    "ALTER TABLE values_table ADD COLUMN checked INTEGER CHECK(missing > 0);");
                invalidCheck.Should().Throw<EmbeddedSqlException>().WithMessage("no such column: missing");

                Action invalidDefault = () => Execute(
                    connection,
                    "ALTER TABLE values_table ADD COLUMN defaulted INTEGER DEFAULT (missing);");
                invalidDefault.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("default value of column [defaulted] is not constant");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                ReadRows(reopenedConnection, "PRAGMA table_info(values_table);").Should().HaveCount(1);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM pragma_table_info('values_table');").Should().Be(1);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void GeneratedColumnUniqueMetadataRoundTripsThroughFileCatalog()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE generated_values(
                        value INTEGER,
                        computed INTEGER GENERATED ALWAYS AS (value + 1) VIRTUAL
                            CONSTRAINT generated_unique UNIQUE ON CONFLICT IGNORE
                    );
                    """);
                Execute(connection, "INSERT INTO generated_values(value) VALUES (1), (1);");
                ScalarInteger(connection, "SELECT COUNT(*) FROM generated_values;").Should().Be(1);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Execute(reopenedConnection, "INSERT INTO generated_values(value) VALUES (3), (3);");
                ReadRows(reopenedConnection, "SELECT value, computed FROM generated_values ORDER BY value;")
                    .Should().BeEquivalentTo(
                    [
                        new[] { SqlValue.Integer(1), SqlValue.Integer(2) },
                        new[] { SqlValue.Integer(3), SqlValue.Integer(4) },
                    ],
                    options => options.WithStrictOrdering());
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'generated_values';")
                .Should().Contain("CONSTRAINT generated_unique UNIQUE ON CONFLICT IGNORE");
            ScalarInteger(
                sqlite,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sqlite_autoindex_generated_values_1' AND sql IS NULL;")
                .Should().Be(1);
            Execute(sqlite, "INSERT INTO generated_values(value) VALUES (2), (2);");
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM generated_values;").Should().Be(3);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void CompositePrimaryKeyOrderConflictAndConstraintMetadataRoundTripThroughFileCatalog()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE keyed(
                        tenant TEXT,
                        sequence INTEGER,
                        payload TEXT CONSTRAINT payload_default DEFAULT 'ready',
                        CONSTRAINT positive_sequence CHECK (sequence > 0) ON CONFLICT FAIL,
                        CONSTRAINT keyed_pk PRIMARY KEY(sequence DESC, tenant ASC) ON CONFLICT IGNORE
                    );
                    """);
                Execute(connection, "INSERT INTO keyed(tenant, sequence) VALUES ('a', 1), ('a', 1), ('b', 2);");
                ScalarInteger(connection, "SELECT COUNT(*) FROM keyed;").Should().Be(2);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Execute(connection, "INSERT INTO keyed(tenant, sequence) VALUES ('c', 3), ('c', 3);");
                ScalarInteger(connection, "SELECT COUNT(*) FROM keyed;").Should().Be(3);
                ReadRows(connection, "PRAGMA index_list(keyed);").Single()[3].AsText().Should().Be("pk");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarText(sqlite, "SELECT origin FROM pragma_index_list('keyed');").Should().Be("pk");
            var primaryKeyIndex = ScalarText(
                sqlite,
                "SELECT name FROM pragma_index_list('keyed') WHERE origin = 'pk';");
            ScalarInteger(sqlite, $"SELECT desc FROM pragma_index_xinfo('{primaryKeyIndex}') WHERE seqno = 0;")
                .Should().Be(1);
            ScalarInteger(sqlite, $"SELECT desc FROM pragma_index_xinfo('{primaryKeyIndex}') WHERE seqno = 1;")
                .Should().Be(0);
            var schemaSql = ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'keyed';");
            schemaSql.Should().Contain("CONSTRAINT payload_default DEFAULT 'ready'")
                .And.Contain("CONSTRAINT positive_sequence CHECK (sequence > 0) ON CONFLICT FAIL")
                .And.Contain("CONSTRAINT keyed_pk PRIMARY KEY(sequence DESC, tenant ASC) ON CONFLICT IGNORE");
            Execute(sqlite, "INSERT INTO keyed(tenant, sequence) VALUES ('d', 4), ('d', 4);");
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM keyed;").Should().Be(4);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void TableIntegerPrimaryKeyIsAConflictAwareRowidAliasWithoutAnAutoindex()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    "CREATE TABLE keyed(id INTEGER, payload TEXT, PRIMARY KEY(id DESC) ON CONFLICT IGNORE);");
                Execute(connection, "INSERT INTO keyed VALUES (5, 'first'), (5, 'ignored');");
                Execute(connection, "UPDATE keyed SET rowid = 99 WHERE id = 5;");
                var row = ReadRows(connection, "SELECT rowid, id, payload FROM keyed;").Single();
                row.Should().Equal(SqlValue.Integer(99), SqlValue.Integer(99), SqlValue.Text("first"));
                ReadRows(connection, "PRAGMA index_list(keyed);").Should().BeEmpty();
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarInteger(sqlite, "SELECT rowid FROM keyed;").Should().Be(99);
            ScalarInteger(sqlite, "SELECT id FROM keyed;").Should().Be(99);
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM pragma_index_list('keyed');").Should().Be(0);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void NamedColumnConstraintKindsRoundTripThroughFileCatalog()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY);");
                Execute(
                    connection,
                    """
                    CREATE TABLE detail(
                        value TEXT
                            CONSTRAINT named_null NULL
                            CONSTRAINT named_collation COLLATE NOCASE
                            CONSTRAINT named_default DEFAULT 'ready',
                        parent_id INTEGER
                            CONSTRAINT named_reference REFERENCES parent(id)
                    );
                    """);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Execute(connection, "INSERT INTO parent VALUES (1);");
                Execute(connection, "INSERT INTO detail(parent_id) VALUES (1);");
                ReadRows(connection, "SELECT value FROM detail;").Single()[0].AsText().Should().Be("ready");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'detail';")
                .Should().Contain("CONSTRAINT named_null NULL")
                .And.Contain("CONSTRAINT named_collation COLLATE NOCASE")
                .And.Contain("CONSTRAINT named_default DEFAULT 'ready'")
                .And.Contain("CONSTRAINT named_reference REFERENCES parent(id)");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void TableQualifiedChecksMatchSqliteAndSurviveReopen()
    {
        const string create = "CREATE TABLE qualified(value INTEGER, CHECK(qualified.value > 0));";
        AssertQueryMatchesSqlite(
            [create, "INSERT INTO qualified VALUES (1);"],
            "SELECT value FROM qualified;");

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(connection, "INSERT INTO qualified VALUES (1);");
                Action invalid = () => Execute(connection, "INSERT INTO qualified VALUES (0);");
                invalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: qualified.value > 0");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Action reopenedInvalid = () => Execute(reopenedConnection, "INSERT INTO qualified VALUES (-1);");
                reopenedInvalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: qualified.value > 0");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Action sqliteInvalid = () => Execute(sqlite, "INSERT INTO qualified VALUES (0);");
            sqliteInvalid.Should().Throw<MsData.SqliteException>()
                .WithMessage("*CHECK constraint failed: qualified.value > 0*");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void TableRenameRewritesQualifiedChecksAndSurvivesReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE old_name(value INTEGER, CHECK(old_name.value > 0));");
                Execute(connection, "ALTER TABLE old_name RENAME TO new_name;");

                ScalarText(connection, "SELECT sql FROM sqlite_schema WHERE name = 'new_name';")
                    .Should().Contain("CHECK(\"new_name\".value > 0)");
                Execute(connection, "INSERT INTO new_name VALUES (1);");
                Action invalid = () => Execute(connection, "INSERT INTO new_name VALUES (0);");
                invalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: \"new_name\".value > 0");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Action reopenedInvalid = () => Execute(reopenedConnection, "INSERT INTO new_name VALUES (-1);");
                reopenedInvalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: \"new_name\".value > 0");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'old_name';").Should().Be(0);
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'new_name';").Should().Be(1);
            ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 'new_name';")
                .Should().Contain("CHECK(\"new_name\".value > 0)");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void CheckRowidAliasesMatchSqliteShadowingAndSurviveReopen()
    {
        const string create =
            "CREATE TABLE rowid_checks(value INTEGER, CHECK(rowid > 0), CHECK(rowid_checks._rowid_ > 0), CHECK(oid > 0));";
        AssertQueryMatchesSqlite(
            [create, "INSERT INTO rowid_checks(value) VALUES (1);"],
            "SELECT value FROM rowid_checks;");

        using (var managedDatabase = new EmbeddedDatabase())
        using (var managed = managedDatabase.Connect())
        {
            Action invalidWithoutRowid = () => Execute(
                managed,
                "CREATE TABLE invalid(value INTEGER PRIMARY KEY, CHECK(rowid > 0)) WITHOUT ROWID;");
            invalidWithoutRowid.Should().Throw<EmbeddedSqlException>().WithMessage("no such column: rowid");
            Execute(
                managed,
                "CREATE TABLE shadowed(rowid INTEGER, value INTEGER, CHECK(rowid > 0), CHECK(shadowed._rowid_ > 0));");
            Execute(managed, "INSERT INTO shadowed(rowid, value) VALUES (1, 1);");
            Action hiddenRowidViolation = () => Execute(
                managed,
                "INSERT INTO shadowed(_rowid_, rowid, value) VALUES (-1, 1, 2);");
            hiddenRowidViolation.Should().Throw<EmbeddedSqlException>()
                .WithMessage("CHECK constraint failed: shadowed._rowid_ > 0");
        }

        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Action invalidWithoutRowid = () => Execute(
                sqlite,
                "CREATE TABLE invalid(value INTEGER PRIMARY KEY, CHECK(rowid > 0)) WITHOUT ROWID;");
            invalidWithoutRowid.Should().Throw<MsData.SqliteException>().WithMessage("*no such column: rowid*");
        }

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(connection, "INSERT INTO rowid_checks(rowid, value) VALUES (1, 1);");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Action reopenedInvalid = () => Execute(
                    reopenedConnection,
                    "INSERT INTO rowid_checks(rowid, value) VALUES (-2, 3);");
                reopenedInvalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: rowid > 0");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Action invalid = () => Execute(sqlite, "INSERT INTO rowid_checks(rowid, value) VALUES (-1, 2);");
            invalid.Should().Throw<MsData.SqliteException>()
                .WithMessage("*CHECK constraint failed: rowid > 0*");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void NotNullIgnoreUpdateUsesRowwiseConflictPolicyAfterReopen()
    {
        AssertQueryMatchesSqlite(
            [
                "CREATE TABLE configured(value INTEGER NOT NULL ON CONFLICT IGNORE);",
                "INSERT INTO configured VALUES (1), (2);",
                "UPDATE configured SET value = NULL;",
            ],
            "SELECT value FROM configured ORDER BY value;");

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE configured(value INTEGER NOT NULL ON CONFLICT IGNORE);");
                Execute(connection, "INSERT INTO configured VALUES (1), (2);");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Execute(reopenedConnection, "UPDATE configured SET value = NULL;");
                ReadRows(reopenedConnection, "SELECT value FROM configured ORDER BY value;")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(1, 2);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            Execute(sqlite, "UPDATE configured SET value = NULL;");
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM configured WHERE value IS NOT NULL;").Should().Be(2);
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void GeneratedNotNullIgnoreSkipsInvalidInsertAndUpdateRows()
    {
        const string create =
            """
            CREATE TABLE generated_not_null(
                id INTEGER,
                value INTEGER,
                computed INTEGER GENERATED ALWAYS AS (
                    CASE WHEN value > 0 THEN value END
                ) VIRTUAL NOT NULL ON CONFLICT IGNORE
            );
            """;
        AssertQueryMatchesSqlite(
            [
                create,
                "INSERT INTO generated_not_null(id, value) VALUES (1, 1), (2, 0), (3, 3);",
                "UPDATE generated_not_null SET value = 0;",
            ],
            "SELECT id, value, computed FROM generated_not_null ORDER BY id;");

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(
                    connection,
                    "INSERT INTO generated_not_null(id, value) VALUES (1, 1), (2, 0), (3, 3);");
                ReadRows(connection, "SELECT id FROM generated_not_null ORDER BY id;")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(1, 3);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Execute(
                    reopenedConnection,
                    "INSERT INTO generated_not_null(id, value) VALUES (4, 4), (5, 0), (6, 6);");
                Execute(reopenedConnection, "UPDATE generated_not_null SET value = 0 WHERE id >= 4;");
                ReadRows(reopenedConnection, "SELECT id, value, computed FROM generated_not_null ORDER BY id;")
                    .Should().BeEquivalentTo(
                    [
                        new[] { SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1) },
                        new[] { SqlValue.Integer(3), SqlValue.Integer(3), SqlValue.Integer(3) },
                        new[] { SqlValue.Integer(4), SqlValue.Integer(4), SqlValue.Integer(4) },
                        new[] { SqlValue.Integer(6), SqlValue.Integer(6), SqlValue.Integer(6) },
                    ],
                    options => options.WithStrictOrdering());
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            Execute(sqlite, "UPDATE generated_not_null SET value = 0;");
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM generated_not_null WHERE value > 0;").Should().Be(4);
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void QuotedDottedCheckIdentifierIsNotTreatedAsQualification()
    {
        const string create =
            """CREATE TABLE dotted("dotted.value" INTEGER, value INTEGER, CHECK("dotted.value" > 0));""";
        AssertQueryMatchesSqlite(
            [create, """INSERT INTO dotted("dotted.value", value) VALUES (1, -1);"""],
            "SELECT COUNT(*) FROM dotted;");

        using (var managedDatabase = new EmbeddedDatabase())
        using (var managed = managedDatabase.Connect())
        {
            Execute(managed, create);
            Action invalid = () => Execute(
                managed,
                """INSERT INTO dotted("dotted.value", value) VALUES (-1, 1);""");
            invalid.Should().Throw<EmbeddedSqlException>()
                .WithMessage("CHECK constraint failed: \"dotted.value\" > 0");
        }

        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Execute(sqlite, create);
            Action invalid = () => Execute(
                sqlite,
                """INSERT INTO dotted("dotted.value", value) VALUES (-1, 1);""");
            invalid.Should().Throw<MsData.SqliteException>()
                .WithMessage("*CHECK constraint failed: dotted.value*");
        }

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(connection, """INSERT INTO dotted("dotted.value", value) VALUES (1, -1);""");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Action reopenedInvalid = () => Execute(
                    reopenedConnection,
                    """INSERT INTO dotted("dotted.value", value) VALUES (0, 1);""");
                reopenedInvalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("CHECK constraint failed: \"dotted.value\" > 0");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void UniqueIgnoreUpdateScansRowidOrderAfterOutOfOrderInsertion()
    {
        const string create =
            "CREATE TABLE ordered(id INTEGER PRIMARY KEY, value INTEGER UNIQUE ON CONFLICT IGNORE);";
        AssertQueryMatchesSqlite(
            [
                create,
                "INSERT INTO ordered VALUES (2, 2), (1, 1);",
                "UPDATE ordered SET value = 0;",
            ],
            "SELECT id, value FROM ordered ORDER BY id;");

        using (var database = new EmbeddedDatabase())
        using (var connection = database.Connect())
        {
            Execute(connection, create);
            Execute(connection, "INSERT INTO ordered VALUES (2, 2), (1, 1);");
            Execute(connection, "UPDATE ordered SET value = 0;");
            ReadRows(connection, "SELECT id, value FROM ordered ORDER BY id;")
                .Should().BeEquivalentTo(
                [
                    new[] { SqlValue.Integer(1), SqlValue.Integer(0) },
                    new[] { SqlValue.Integer(2), SqlValue.Integer(2) },
                ],
                options => options.WithStrictOrdering());
        }

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(connection, "INSERT INTO ordered VALUES (2, 2), (1, 1);");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Execute(connection, "INSERT INTO ordered VALUES (4, 4), (3, 3);");
                Execute(connection, "UPDATE ordered SET value = 10 WHERE id >= 3;");
                ReadRows(connection, "SELECT id, value FROM ordered ORDER BY id;")
                    .Should().BeEquivalentTo(
                    [
                        new[] { SqlValue.Integer(1), SqlValue.Integer(1) },
                        new[] { SqlValue.Integer(2), SqlValue.Integer(2) },
                        new[] { SqlValue.Integer(3), SqlValue.Integer(10) },
                        new[] { SqlValue.Integer(4), SqlValue.Integer(4) },
                    ],
                    options => options.WithStrictOrdering());
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void UnrelatedUniqueIgnoreDoesNotChangeHiddenRowidConflictPolicy()
    {
        const string create = "CREATE TABLE hidden_rowid(code TEXT UNIQUE ON CONFLICT IGNORE);";
        const string duplicate = "INSERT INTO hidden_rowid(rowid, code) VALUES (1, 'b');";
        AssertQueryMatchesSqlite(
            [
                "CREATE TABLE rowid_ignore(id INTEGER PRIMARY KEY ON CONFLICT IGNORE, code TEXT);",
                "INSERT INTO rowid_ignore VALUES (1, 'a');",
                "INSERT INTO rowid_ignore VALUES (1, 'b');",
            ],
            "SELECT id, code FROM rowid_ignore;");
        AssertQueryMatchesSqlite(
            [
                "CREATE TABLE rowid_replace(id INTEGER PRIMARY KEY ON CONFLICT REPLACE, code TEXT);",
                "INSERT INTO rowid_replace VALUES (1, 'a');",
                "INSERT INTO rowid_replace VALUES (1, 'b');",
            ],
            "SELECT id, code FROM rowid_replace;");

        using (var managedDatabase = new EmbeddedDatabase())
        using (var managed = managedDatabase.Connect())
        using (var sqlite = new MsData.SqliteConnection("Data Source=:memory:"))
        {
            sqlite.Open();
            Execute(managed, create);
            Execute(sqlite, create);
            Execute(managed, "INSERT INTO hidden_rowid(rowid, code) VALUES (1, 'a');");
            Execute(sqlite, "INSERT INTO hidden_rowid(rowid, code) VALUES (1, 'a');");

            var managedError = Assert.Throws<EmbeddedSqlException>(() => Execute(managed, duplicate));
            var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, duplicate));
            sqliteError.Message.Should().Contain(managedError!.Message);
            ScalarInteger(managed, "SELECT COUNT(*) FROM hidden_rowid;").Should().Be(1);
            ScalarInteger(sqlite, "SELECT COUNT(*) FROM hidden_rowid;").Should().Be(1);
        }

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(connection, "INSERT INTO hidden_rowid(rowid, code) VALUES (1, 'a');");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Action invalid = () => Execute(connection, duplicate);
                invalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("UNIQUE constraint failed: hidden_rowid.rowid");
                ScalarInteger(connection, "SELECT COUNT(*) FROM hidden_rowid;").Should().Be(1);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void QuotedDottedTableQualifierResolvesHiddenRowidCheck()
    {
        const string create =
            """CREATE TABLE "a.b"(value INTEGER, CHECK("a.b".rowid > 0));""";
        AssertQueryMatchesSqlite(
            [create, """INSERT INTO "a.b"(rowid, value) VALUES (1, 1);"""],
            """SELECT rowid, value FROM "a.b";""");

        var path = CreateDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, create);
                Execute(connection, """INSERT INTO "a.b"(rowid, value) VALUES (1, 1);""");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var reopenedConnection = reopened.Connect())
            {
                Execute(reopenedConnection, """INSERT INTO "a.b"(rowid, value) VALUES (3, 3);""");
                Action invalid = () => Execute(
                    reopenedConnection,
                    """INSERT INTO "a.b"(rowid, value) VALUES (-1, 4);""");
                invalid.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("""CHECK constraint failed: "a.b".rowid > 0""");
                ReadRows(reopenedConnection, """SELECT rowid, value FROM "a.b" ORDER BY rowid;""")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(1, 3);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            Execute(sqlite, """INSERT INTO "a.b"(rowid, value) VALUES (2, 2);""");
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ReadRows(sqlite, """SELECT rowid, value FROM "a.b" ORDER BY rowid;""")
                .Select(row => Convert.ToInt64(row[0]))
                .Should().Equal(1, 2, 3);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static void AssertQueryMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Should().BeEquivalentTo(sqlite, options => options.WithStrictOrdering());
    }

    private static void AssertErrorMatchesSqlite(IReadOnlyList<string> setup, string command)
    {
        using var managedDatabase = new EmbeddedDatabase();
        using var managed = managedDatabase.Connect();
        foreach (var sql in setup)
            Execute(managed, sql);

        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        foreach (var sql in setup)
            Execute(sqlite, sql);

        var managedError = Assert.Catch<EmbeddedSqlException>(() => Execute(managed, command))!;
        var sqliteError = Assert.Throws<MsData.SqliteException>(() => Execute(sqlite, command))!;
        sqliteError.Message.Should().Contain(managedError.Message);

        ReadRows(managed, "SELECT id, quantity, limit_value FROM items ORDER BY id;")
            .Select(row => row.Select(ToObject).ToArray())
            .Should().BeEquivalentTo(
                ReadRows(sqlite, "SELECT id, quantity, limit_value FROM items ORDER BY id;"),
                options => options.WithStrictOrdering());
    }

    private static IReadOnlyList<object?[]> RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var sql in setup)
            Execute(connection, sql);
        return ReadRows(connection, query).Select(row => row.Select(ToObject).ToArray()).ToArray();
    }

    private static IReadOnlyList<object?[]> RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
            Execute(connection, sql);
        return ReadRows(connection, query);
    }

    private static object? ToObject(SqlValue value)
        => value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Integer => value.AsInteger(),
            SqlValueKind.Real => value.AsReal(),
            SqlValueKind.Text => value.AsText(),
            SqlValueKind.Blob => value.AsBlob().ToArray(),
            _ => throw new InvalidOperationException($"Unknown SQL value kind {value.Kind}."),
        };

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.ColumnCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = statement.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static IReadOnlyList<object?[]> ReadRows(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static long ScalarInteger(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsInteger();

    private static long ScalarInteger(MsData.SqliteConnection connection, string sql)
        => Convert.ToInt64(Scalar(connection, sql));

    private static string ScalarText(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single().Single().AsText();

    private static string ScalarText(MsData.SqliteConnection connection, string sql)
        => (string)Scalar(connection, sql)!;

    private static object? Scalar(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "managed-constraint-semantics");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
