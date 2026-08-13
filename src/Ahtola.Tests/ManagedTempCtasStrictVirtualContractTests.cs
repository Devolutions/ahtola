using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedTempCtasStrictVirtualContractTests
{
    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void TempShadowingSchemaAndTransactionBehaviorMatchesSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE t(value TEXT)",
            "INSERT INTO main.t VALUES ('main')",
            "CREATE TEMPORARY TABLE t(value TEXT)",
            "INSERT INTO temp.t VALUES ('temp')",
            "BEGIN",
            "INSERT INTO temp.t VALUES ('temp-committed')",
            "INSERT INTO main.t VALUES ('main-committed')",
            "SAVEPOINT keep",
            "INSERT INTO temp.t VALUES ('rolled-back')",
            "ROLLBACK TO keep",
            "RELEASE keep",
            "COMMIT",
        ];

        AssertMatchesSqlite(setup, "SELECT value FROM t ORDER BY value");
        AssertMatchesSqlite(setup, "SELECT value FROM main.t ORDER BY value");
        AssertMatchesSqlite(
            setup,
            "SELECT type,name,tbl_name FROM temp.sqlite_schema ORDER BY type,name");
        AssertMatchesSqlite(setup, "PRAGMA database_list");
        AssertMatchesSqlite(setup, "PRAGMA temp.table_info(t)");
    }

    [Test]
    public void TempCatalogIsIsolatedPerConnection()
    {
        using var database = new EmbeddedDatabase();
        using var first = database.Connect();
        using var second = database.Connect();
        Execute(first, "CREATE TABLE main.items(value TEXT); INSERT INTO main.items VALUES ('main');");
        Execute(first, "CREATE TEMP TABLE items(value TEXT); INSERT INTO temp.items VALUES ('first');");

        ReadScalar(first, "SELECT value FROM items;").Should().Be(SqlValue.Text("first"));
        ReadScalar(second, "SELECT value FROM items;").Should().Be(SqlValue.Text("main"));
        ReadScalar(second, "SELECT count(*) FROM temp.sqlite_schema;").Should().Be(SqlValue.Integer(0));
        ReadScalar(second, "SELECT count(*) FROM sqlite_temp_schema;").Should().Be(SqlValue.Integer(0));
        var wrongSchema = () => ReadScalar(second, "SELECT count(*) FROM main.sqlite_temp_schema;");
        wrongSchema.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: sqlite_temp_schema");

        using var third = database.Connect();
        ReadScalar(third, "SELECT value FROM items;").Should().Be(SqlValue.Text("main"));
    }

    [Test]
    public void TempDatabaseListLifetimeAndTableListNamesMatchSqlite()
    {
        string[] setup =
        [
            "BEGIN",
            "CREATE TEMP TABLE transient(value INTEGER)",
            "ROLLBACK",
        ];

        AssertMatchesSqlite(setup, "PRAGMA database_list");
        AssertMatchesSqlite(setup, "PRAGMA table_list");
    }

    [Test]
    public void FailedTempLookupDoesNotInitializeDatabaseList()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var missing = () => ReadRows(connection, "SELECT * FROM temp.missing;");
        missing.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: missing");
        ReadRows(connection, "PRAGMA database_list;")
            .Should().ContainSingle().Which[1].Should().Be(SqlValue.Text("main"));
    }

    [Test]
    public void PoolResetDropsTempCatalogWithoutDroppingMain()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={path};Pooling=True;Local Provider=Managed");
            connection.Open();
            connection.ExecuteNonQuery("CREATE TABLE main_data(value INTEGER); INSERT INTO main_data VALUES (1);");
            connection.ExecuteNonQuery("CREATE TEMP TABLE temp_data(value INTEGER); INSERT INTO temp_data VALUES (2);");
            var physical = connection.ManagedConnection;

            connection.Close();
            connection.Open();

            connection.ManagedConnection.Should().BeSameAs(physical);
            connection.ExecuteScalar<long>("SELECT value FROM main_data;").Should().Be(1);
            connection.Invoking(value => value.ExecuteScalar<long>("SELECT value FROM temp_data;"))
                .Should().Throw<SqliteException>().WithMessage("*no such table: temp_data*");
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void BackupAndReopenExcludeTempCatalog()
    {
        var sourcePath = CreatePhysicalDatabasePath();
        var destinationPath = CreatePhysicalDatabasePath();
        try
        {
            using (var source = OpenManaged(sourcePath))
            using (var destination = OpenManaged(destinationPath))
            {
                source.ExecuteNonQuery(
                    "CREATE TABLE persistent(value TEXT) STRICT; INSERT INTO persistent VALUES ('kept');"
                    + "CREATE TABLE materialized AS SELECT value FROM persistent;"
                    + "CREATE TEMP TABLE transient(value TEXT); INSERT INTO transient VALUES ('excluded');");

                source.BackupDatabase(destination);

                destination.ExecuteScalar<string>("SELECT value FROM persistent;").Should().Be("kept");
                destination.ExecuteScalar<string>("SELECT value FROM materialized;").Should().Be("kept");
                using (var tableList = destination.ExecuteReader("PRAGMA table_list;"))
                {
                    var foundStrictTable = false;
                    while (tableList.Read())
                    {
                        foundStrictTable |= tableList.GetString(1) == "persistent"
                            && tableList.GetInt64(5) == 1;
                    }
                    foundStrictTable.Should().BeTrue();
                }
                destination.Invoking(value => value.ExecuteScalar<string>("SELECT value FROM transient;"))
                    .Should().Throw<SqliteException>().WithMessage("*no such table: transient*");
                source.Invoking(value => value.BackupDatabase(destination, "main", "temp"))
                    .Should().Throw<SqliteException>()
                    .WithMessage("*backup excludes the connection-private temporary database*");
                destination.ExecuteScalar<string>("SELECT value FROM persistent;").Should().Be("kept");
            }

            using var reopened = OpenManaged(sourcePath);
            reopened.ExecuteScalar<string>("SELECT value FROM persistent;").Should().Be("kept");
            reopened.Invoking(value => value.ExecuteScalar<string>("SELECT value FROM transient;"))
                .Should().Throw<SqliteException>().WithMessage("*no such table: transient*");
        }
        finally
        {
            DeletePhysicalDatabase(sourcePath);
            DeletePhysicalDatabase(destinationPath);
        }
    }

    [Test]
    public void TempAndAttachedSchemasUseExplicitNamesForCrossSchemaCtas()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("temp-attach-main.db", fileSystem);
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE items(value TEXT); INSERT INTO items VALUES ('main');"
            + "ATTACH DATABASE 'temp-attach-aux.db' AS aux;"
            + "CREATE TABLE aux.items(value TEXT); INSERT INTO aux.items VALUES ('aux');"
            + "CREATE TEMP TABLE items(value TEXT); INSERT INTO temp.items VALUES ('temp');"
            + "CREATE INDEX temp.items_index ON items(value);"
            + "CREATE TABLE aux.from_temp AS SELECT value FROM temp.items;"
            + "CREATE TEMP TABLE from_aux AS SELECT value FROM aux.items;"
            + "CREATE TABLE aux.only_aux(value TEXT);");

        ReadScalar(connection, "SELECT value FROM items;").Should().Be(SqlValue.Text("temp"));
        ReadScalar(connection, "SELECT value FROM main.items;").Should().Be(SqlValue.Text("main"));
        ReadScalar(connection, "SELECT value FROM aux.items;").Should().Be(SqlValue.Text("aux"));
        ReadScalar(connection, "SELECT value FROM aux.from_temp;").Should().Be(SqlValue.Text("temp"));
        ReadScalar(connection, "SELECT value FROM temp.from_aux;").Should().Be(SqlValue.Text("aux"));
        ReadScalar(connection, "SELECT count(*) FROM temp.sqlite_schema WHERE name='items_index';")
            .Should().Be(SqlValue.Integer(1));
        ReadRows(connection, "PRAGMA database_list;").Select(row => row[1].AsText())
            .Should().Equal("main", "temp", "aux");

        var unqualifiedAttachedIndex = () => Execute(connection, "CREATE INDEX only_aux_index ON only_aux(value);");
        unqualifiedAttachedIndex.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: only_aux");
        Execute(connection, "CREATE INDEX aux.only_aux_index ON only_aux(value);");
    }

    [Test]
    public void TempStrictCompositeForeignKeysUseThePrivateCatalog()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "PRAGMA foreign_keys=ON;"
            + "CREATE TEMP TABLE parent(tenant TEXT,code INT,"
            + "PRIMARY KEY(tenant,code)) WITHOUT ROWID, STRICT;"
            + "CREATE TEMP TABLE child(tenant TEXT,code INT,"
            + "FOREIGN KEY(tenant,code) REFERENCES parent(tenant,code) ON UPDATE CASCADE) STRICT;"
            + "INSERT INTO parent VALUES('acme',1);"
            + "INSERT INTO child VALUES('acme',1);"
            + "UPDATE parent SET code=2 WHERE tenant='acme';");

        ReadScalar(connection, "SELECT code FROM child;").Should().Be(SqlValue.Integer(2));
        ReadRows(connection, "PRAGMA temp.foreign_key_list(child);")
            .Should().HaveCount(2)
            .And.OnlyContain(row => row[2] == SqlValue.Text("parent"));
        ReadRows(connection, "PRAGMA temp.foreign_key_check;").Should().BeEmpty();

        var violation = () => Execute(connection, "INSERT INTO child VALUES('missing',9);");
        violation.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
    }

    [Test]
    public void CtasDeclaredTypesNamesRowsAndEmptyResultsMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE src(i INTEGER, n NUMERIC, r REAL, t TEXT, b BLOB, v VARCHAR(10))",
            "INSERT INTO src VALUES(1,2,3.5,'4',x'05','6')",
            "CREATE TABLE dst AS SELECT i,n,r,t,b,v,CAST(i AS TEXT) AS c,i+0 AS x,"
                + "(SELECT i) AS sub_i,1 AS lit FROM src",
            "CREATE TABLE duplicate_names AS SELECT 1 AS x,2 AS X,3 AS \"x:1\"",
            "CREATE TABLE empty_copy AS SELECT i,t FROM src WHERE 0",
        ];

        AssertMatchesSqlite(setup, "PRAGMA table_info(dst)");
        AssertMatchesSqlite(setup, "SELECT rowid,* FROM dst");
        AssertMatchesSqlite(setup, "PRAGMA table_info(duplicate_names)");
        AssertMatchesSqlite(setup, "PRAGMA table_info(empty_copy)");
    }

    [Test]
    public void CtasFromStrictAnyKeepsNoAffinity()
    {
        string[] setup =
        [
            "CREATE TABLE source(value ANY) STRICT",
            "INSERT INTO source VALUES ('0006')",
            "CREATE TABLE copy AS SELECT value FROM source",
        ];

        AssertMatchesSqlite(setup, "PRAGMA table_info(copy)");
        AssertMatchesSqlite(setup, "SELECT value,typeof(value) AS value_type FROM copy");
    }

    [Test]
    public void CtasFailureAndCancellationDoNotPublishCatalogEntries()
    {
        using var cancellation = new CancellationTokenSource();
        using var database = new EmbeddedDatabase();
        database.RegisterScalarFunction(
            "cancel_now",
            1,
            values =>
            {
                cancellation.Cancel();
                return values[0];
            });
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE source(value INTEGER); INSERT INTO source VALUES (1),(2);");

        var missing = () => Execute(connection, "CREATE TABLE missing_copy AS SELECT * FROM no_such_source;");
        missing.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: no_such_source");
        ReadScalar(connection, "SELECT count(*) FROM sqlite_schema WHERE name='missing_copy';")
            .Should().Be(SqlValue.Integer(0));

        using (var statement = connection.Prepare(
                   "CREATE TABLE cancelled_copy AS SELECT cancel_now(value) AS value FROM source;"))
        {
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        }
        ReadScalar(connection, "SELECT count(*) FROM sqlite_schema WHERE name='cancelled_copy';")
            .Should().Be(SqlValue.Integer(0));
    }

    [Test]
    public void CtasParticipatesInSavepointRollback()
    {
        string[] setup =
        [
            "CREATE TABLE source(value INTEGER)",
            "INSERT INTO source VALUES (1),(2)",
            "BEGIN",
            "SAVEPOINT before_copy",
            "CREATE TABLE rolled_back AS SELECT * FROM source",
            "ROLLBACK TO before_copy",
            "RELEASE before_copy",
            "CREATE TABLE committed AS SELECT * FROM source",
            "COMMIT",
        ];

        AssertMatchesSqlite(setup, "SELECT name FROM sqlite_schema WHERE name IN ('rolled_back','committed')");
        AssertMatchesSqlite(setup, "SELECT rowid,value FROM committed ORDER BY rowid");
    }

    [Test]
    public void CtasCopiesValuesButNotGeneratedColumnsConstraintsForeignKeysOrTriggers()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys=ON",
            "CREATE TABLE parent(id INTEGER PRIMARY KEY)",
            "INSERT INTO parent VALUES (1)",
            "CREATE TABLE source(id INTEGER PRIMARY KEY,parent_id INTEGER REFERENCES parent(id),"
                + "base INT NOT NULL CHECK(base>0),doubled INT AS(base*2) VIRTUAL) STRICT",
            "CREATE TABLE audit(value INTEGER)",
            "CREATE TRIGGER source_ai AFTER INSERT ON source BEGIN INSERT INTO audit VALUES (1); END",
            "INSERT INTO source(id,parent_id,base) VALUES (1,1,3)",
            "CREATE TABLE copy AS SELECT * FROM source",
            "INSERT INTO copy VALUES (1,NULL,NULL,NULL)",
        ];

        AssertMatchesSqlite(setup, "PRAGMA table_xinfo(copy)");
        AssertMatchesSqlite(setup, "SELECT rowid,* FROM copy ORDER BY rowid");
        AssertMatchesSqlite(
            setup,
            "SELECT count(*) AS trigger_count FROM sqlite_schema WHERE type='trigger' AND tbl_name='copy'");
        AssertMatchesSqlite(setup, "SELECT count(*) AS audit_count FROM audit");
    }

    [Test]
    public void CtasMaterializesWindowJoinAndCompoundResults()
    {
        string[] setup =
        [
            "CREATE TABLE numbers(id INTEGER,value INTEGER)",
            "INSERT INTO numbers VALUES (1,10),(2,20),(3,30)",
            "CREATE TABLE labels(id INTEGER,label TEXT)",
            "INSERT INTO labels VALUES (1,'one'),(3,'three')",
            "CREATE TABLE window_copy AS "
                + "SELECT id,sum(value) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS running FROM numbers",
            "CREATE TABLE join_copy AS "
                + "SELECT numbers.id,labels.label FROM numbers LEFT JOIN labels ON labels.id=numbers.id",
            "CREATE TABLE compound_copy AS "
                + "SELECT id,value FROM numbers WHERE id<=2 UNION ALL SELECT id,value FROM numbers WHERE id=3",
        ];

        AssertMatchesSqlite(setup, "SELECT rowid,* FROM window_copy ORDER BY rowid");
        AssertMatchesSqlite(setup, "SELECT rowid,* FROM join_copy ORDER BY rowid");
        AssertMatchesSqlite(setup, "SELECT rowid,* FROM compound_copy ORDER BY rowid");
    }

    [Test]
    public void TempTablesComposeWindowAndExpressionOperators()
    {
        string[] setup =
        [
            "CREATE TEMP TABLE bits(id INTEGER,flags INTEGER)",
            "INSERT INTO temp.bits VALUES (1,2),(2,3),(3,4)",
        ];

        AssertMatchesSqlite(
            setup,
            """
            SELECT id,(flags << 1) | 1 AS shifted,
                   sum(flags) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS running
            FROM temp.bits
            WHERE (id,flags) >= (1,2)
            ORDER BY id
            """);
    }

    [Test]
    public void StrictRowValuesBitwiseDefaultsGeneratedColumnsAndChecksCompose()
    {
        string[] setup =
        [
            """
            CREATE TABLE strict_ops(
                id INT PRIMARY KEY,
                left_value INT DEFAULT (1 << 3),
                right_value INT,
                packed INT AS ((left_value << 4) | right_value) VIRTUAL,
                CHECK ((left_value,right_value) >= (0,0) AND (left_value & 1) = 0)
            ) STRICT
            """,
            "INSERT INTO strict_ops(id,right_value) VALUES (1,2)",
            "UPDATE strict_ops SET (left_value,right_value)=(right_value,left_value) WHERE id=1",
        ];

        AssertMatchesSqlite(
            setup,
            "SELECT id,left_value,right_value,packed,"
                + "(left_value,right_value)=(2,8) AS row_match FROM strict_ops");
        CaptureManagedError(
                setup,
                "INSERT INTO strict_ops(id,left_value,right_value) VALUES (2,3,1)")
            .Should().Contain("CHECK constraint failed");
        CaptureSqliteError(
                setup,
                "INSERT INTO strict_ops(id,left_value,right_value) VALUES (2,3,1)")
            .Should().Contain("CHECK constraint failed");
    }

    [Test]
    public void StrictAffinityAndAnyPreservationMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE s(a INT,b INTEGER,c REAL,d TEXT,e BLOB,f ANY) STRICT",
            "INSERT INTO s VALUES('12','12.0','3','4',x'05','0006')",
            "INSERT INTO s VALUES(13,14,8.0,8.0,x'06',8.0)",
        ];

        AssertMatchesSqlite(
            setup,
            "SELECT typeof(a) AS ta,typeof(b) AS tb,typeof(c) AS tc,typeof(d) AS td,"
            + "typeof(e) AS te,typeof(f) AS tf,f FROM s");
        AssertMatchesSqlite(setup, "PRAGMA table_info(s)");
    }

    [Test]
    public void StrictWithoutRowidOptionsMatchSqlite()
    {
        string[] setup =
        [
            "CREATE TABLE s(id INT PRIMARY KEY,value TEXT) STRICT, WITHOUT ROWID",
            "INSERT INTO s VALUES(1,'one')",
        ];

        AssertMatchesSqlite(setup, "SELECT * FROM s");
        AssertMatchesSqliteUnordered(setup, "PRAGMA table_list");
        CaptureManagedError([], "CREATE TABLE invalid(id INT) STRICT,")
            .Should().Contain("Expected STRICT or WITHOUT ROWID");
        CaptureSqliteError([], "CREATE TABLE invalid(id INT) STRICT,")
            .Should().Contain("incomplete input");
    }

    [TestCase("INSERT INTO s(a) VALUES(1.5)", "cannot store REAL value in INT column s.a")]
    [TestCase("INSERT INTO s(b) VALUES('x')", "cannot store TEXT value in REAL column s.b")]
    [TestCase("INSERT INTO s(c) VALUES(x'01')", "cannot store BLOB value in TEXT column s.c")]
    [TestCase("INSERT INTO s(d) VALUES('x')", "cannot store TEXT value in BLOB column s.d")]
    public void StrictStorageClassErrorsMatchSqlite(string sql, string expected)
    {
        string[] setup = ["CREATE TABLE s(a INT,b REAL,c TEXT,d BLOB,e ANY) STRICT"];
        CaptureManagedError(setup, sql).Should().Be(expected);
        CaptureSqliteError(setup, sql).Should().Contain(expected);
    }

    [TestCase("CREATE TABLE bad(a VARCHAR) STRICT", "unknown datatype for bad.a: \"VARCHAR\"")]
    [TestCase("CREATE TABLE bad(a) STRICT", "missing datatype for bad.a")]
    public void StrictSchemaErrorsMatchSqlite(string sql, string expected)
    {
        CaptureManagedError([], sql).Should().Be(expected);
        CaptureSqliteError([], sql).Should().Contain(expected);
    }

    [Test]
    public void StrictGeneratedConstraintForeignKeyAndTriggerFailuresAreAtomic()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "PRAGMA foreign_keys=ON;"
            + "CREATE TABLE parent(id INT PRIMARY KEY) STRICT;"
            + "CREATE TABLE child(id INT PRIMARY KEY,parent_id INT REFERENCES parent(id),"
            + "base INT CHECK(base>0),doubled INT AS(base*2) VIRTUAL) STRICT;"
            + "CREATE TABLE audit(value INT) STRICT;"
            + "CREATE TRIGGER child_ai AFTER INSERT ON child BEGIN INSERT INTO audit VALUES ('bad'); END;"
            + "INSERT INTO parent VALUES (1);");

        var triggerFailure = () => Execute(
            connection,
            "INSERT INTO child(id,parent_id,base) VALUES (1,1,3);");
        triggerFailure.Should().Throw<EmbeddedSqlException>()
            .WithMessage("cannot store TEXT value in INT column audit.value");
        ReadScalar(connection, "SELECT count(*) FROM child;").Should().Be(SqlValue.Integer(0));
        ReadScalar(connection, "SELECT count(*) FROM audit;").Should().Be(SqlValue.Integer(0));

        Execute(connection, "DROP TRIGGER child_ai;");
        var foreignKeyFailure = () => Execute(
            connection,
            "INSERT INTO child(id,parent_id,base) VALUES (2,999,3);");
        foreignKeyFailure.Should().Throw<EmbeddedSqlException>().WithMessage("FOREIGN KEY constraint failed");
        var checkFailure = () => Execute(
            connection,
            "INSERT INTO child(id,parent_id,base) VALUES (3,1,-1);");
        checkFailure.Should().Throw<EmbeddedSqlException>().WithMessage("CHECK constraint failed*");

        Execute(connection, "INSERT INTO child(id,parent_id,base) VALUES (4,1,5);");
        ReadScalar(connection, "SELECT doubled FROM child;").Should().Be(SqlValue.Integer(10));
    }

    [Test]
    public void StrictTablePrimaryKeysAreImplicitlyNotNull()
    {
        string[] setup = ["CREATE TABLE s(a INT,b TEXT,PRIMARY KEY(a,b)) STRICT"];
        const string insert = "INSERT INTO s VALUES(NULL,'value')";

        CaptureManagedError(setup, insert).Should().Be("NOT NULL constraint failed: s.a");
        CaptureSqliteError(setup, insert).Should().Contain("NOT NULL constraint failed: s.a");
    }

    [Test]
    public void StrictParentAffinityDoesNotReplaceForeignKeyErrors()
    {
        string[] setup =
        [
            "PRAGMA foreign_keys=ON",
            "CREATE TABLE parent(id INT PRIMARY KEY) STRICT",
            "CREATE TABLE child(parent_id REAL REFERENCES parent(id))",
        ];
        const string insert = "INSERT INTO child VALUES(1.5)";

        CaptureManagedError(setup, insert).Should().Be("FOREIGN KEY constraint failed");
        CaptureSqliteError(setup, insert).Should().Contain("FOREIGN KEY constraint failed");
    }

    [Test]
    public void StrictDefaultsAreValidatedOnlyWhenStored()
    {
        string[] setup =
        [
            "CREATE TABLE s(id INT,data BLOB DEFAULT '') STRICT",
            "INSERT INTO s(id,data) VALUES(1,x'01')",
        ];

        AssertMatchesSqlite(setup, "SELECT id,data FROM s");
        CaptureManagedError(setup, "INSERT INTO s(id) VALUES(2)")
            .Should().Be("cannot store TEXT value in BLOB column s.data");
        CaptureSqliteError(setup, "INSERT INTO s(id) VALUES(2)")
            .Should().Contain("cannot store TEXT value in BLOB column s.data");
    }

    [Test]
    public void StrictInsertOrConflictAlgorithmsRemainSupported()
    {
        string[] setup =
        [
            "CREATE TABLE s(id INTEGER PRIMARY KEY,value INT UNIQUE) STRICT",
            "INSERT INTO s VALUES(1,10)",
            "INSERT OR IGNORE INTO s VALUES(2,10)",
            "INSERT OR REPLACE INTO s VALUES(3,10)",
        ];

        AssertMatchesSqlite(setup, "SELECT id,value FROM s ORDER BY id");
    }

    [TestCase("WAL", 4096)]
    [TestCase("DELETE", 8192)]
    public void StrictAndCtasSurviveJournalReopenAndPageMigration(string journalMode, int pageSize)
    {
        var fileSystem = new InMemoryFileSystem();
        var path = $"strict-{journalMode}.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE strict_data(id INTEGER PRIMARY KEY,value TEXT) STRICT;"
                + "INSERT INTO strict_data VALUES (1,'one');"
                + "CREATE TABLE copied AS SELECT id,value FROM strict_data;"
                + "CREATE TEMP TABLE transient(value TEXT); INSERT INTO transient VALUES ('temp');");
            ReadScalar(connection, $"PRAGMA journal_mode={journalMode};")
                .Should().Be(SqlValue.Text(journalMode.ToLowerInvariant()));
            if (journalMode == "DELETE")
            {
                Execute(connection, $"PRAGMA page_size={pageSize}; VACUUM;");
                ReadScalar(connection, "SELECT value FROM transient;").Should().Be(SqlValue.Text("temp"));
            }
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadScalar(reopenedConnection, "SELECT value FROM strict_data;").Should().Be(SqlValue.Text("one"));
        ReadScalar(reopenedConnection, "SELECT value FROM copied;").Should().Be(SqlValue.Text("one"));
        ReadRows(reopenedConnection, "PRAGMA table_list;")
            .Should().ContainSingle(row =>
                row[0] == SqlValue.Text("main")
                && row[1] == SqlValue.Text("strict_data")
                && row[5] == SqlValue.Integer(1));
        ReadScalar(reopenedConnection, "SELECT sql FROM sqlite_schema WHERE name='strict_data';")
            .AsText().Should().EndWith(" STRICT");
        ReadScalar(reopenedConnection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(pageSize));
        var temp = () => ReadScalar(reopenedConnection, "SELECT value FROM transient;");
        temp.Should().Throw<EmbeddedSqlException>().WithMessage("no such table: transient");
    }

    [Test]
    public void StrictFullForeignKeysAndWithoutRowidSurvivePageMigration()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "strict-full-storage.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "PRAGMA foreign_keys=ON;"
                + "CREATE TABLE parent(tenant TEXT,code INT,"
                + "PRIMARY KEY(tenant DESC,code)) WITHOUT ROWID, STRICT;"
                + "CREATE TABLE child(tenant TEXT,code INT,payload TEXT,"
                + "FOREIGN KEY(tenant,code) REFERENCES parent(tenant,code) "
                + "ON UPDATE CASCADE ON DELETE RESTRICT) STRICT;"
                + "INSERT INTO parent VALUES('acme',1);"
                + "INSERT INTO child VALUES('acme',1,'kept');"
                + "CREATE TABLE copied AS SELECT tenant,code,payload FROM child;"
                + "UPDATE parent SET code=2 WHERE tenant='acme' AND code=1;"
                + "PRAGMA journal_mode=DELETE;"
                + "PRAGMA page_size=8192;"
                + "VACUUM;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        Execute(reopenedConnection, "PRAGMA foreign_keys=ON;");
        ReadRows(reopenedConnection, "SELECT tenant,code,payload FROM child;")
            .Should().ContainSingle().Which.Should().Equal(
                SqlValue.Text("acme"),
                SqlValue.Integer(2),
                SqlValue.Text("kept"));
        ReadScalar(reopenedConnection, "SELECT code FROM copied;").Should().Be(SqlValue.Integer(1));
        ReadScalar(reopenedConnection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(8192));
        ReadScalar(reopenedConnection, "SELECT sql FROM sqlite_schema WHERE name='parent';")
            .AsText().Should().EndWith(" WITHOUT ROWID, STRICT");
        ReadScalar(reopenedConnection, "SELECT sql FROM sqlite_schema WHERE name='child';")
            .AsText().Should().Contain("FOREIGN KEY").And.EndWith(" STRICT");
    }

    [Test]
    public void VirtualTableCreationIsRejectedBeforeMutation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        var schemaVersion = ReadScalar(connection, "PRAGMA schema_version;");

        var create = () => connection.Prepare("CREATE VIRTUAL TABLE docs USING fts5(body);");
        create.Should().Throw<EmbeddedSqlException>().WithMessage(
            "Managed virtual tables are not supported: no module registration, planner, or execution contract is available.*");

        ReadScalar(connection, "PRAGMA schema_version;").Should().Be(schemaVersion);
        ReadScalar(connection, "SELECT count(*) FROM sqlite_schema;").Should().Be(SqlValue.Integer(0));
        connection.Invoking(value => value.Prepare(
                "CREATE VIRTUAL TABLE IF NOT EXISTS temp.docs USING fts5(body);"))
            .Should().Throw<EmbeddedSqlException>().WithMessage("Managed virtual tables are not supported:*");

        using var facade = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        facade.Capabilities.SupportsExtensions.Should().BeFalse();
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Should().HaveCount(sqlite.Rows.Count);
        for (var index = 0; index < sqlite.Rows.Count; index++)
            managed.Rows[index].Should().Equal(sqlite.Rows[index]);
    }

    private static void AssertMatchesSqliteUnordered(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var sqlite = RunSqlite(setup, query);
        managed.Columns.Should().Equal(sqlite.Columns);
        managed.Rows.Select(row => string.Join('\u001f', row)).Order()
            .Should().Equal(sqlite.Rows.Select(row => string.Join('\u001f', row)).Order());
    }

    private static QueryOutput RunManaged(IReadOnlyList<string> setup, string query)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var sql in setup)
            Execute(connection, sql);

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

    private static QueryOutput RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = sql;
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

    private static string CaptureManagedError(IReadOnlyList<string> setup, string sql)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var exception = Assert.Throws<EmbeddedSqlException>(() => Execute(connection, sql));
        return exception!.Message;
    }

    private static string CaptureSqliteError(IReadOnlyList<string> setup, string sql)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var setupCommand = connection.CreateCommand();
            setupCommand.CommandText = statement;
            setupCommand.ExecuteNonQuery();
        }

        var exception = Assert.Throws<MsData.SqliteException>(() =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });
        return exception!.Message;
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

    private static SqlValue ReadScalar(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0);
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
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

    private static SqliteConnection OpenManaged(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static string CreatePhysicalDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"Ahtola-contract-{Guid.NewGuid():N}.db");

    private static void DeletePhysicalDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-journal" })
        {
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<string[]> Rows);
}
