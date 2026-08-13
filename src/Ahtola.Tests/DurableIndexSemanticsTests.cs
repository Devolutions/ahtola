using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;
using Ahtola.Data.Sqlite;
using StorageCipher = Ahtola.Core.Storage.AhtolaEncryptionCipher;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class DurableIndexSemanticsTests
{
    private const string Aes256Key =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void ExplicitAndConstraintIndexesRoundTripWithSqliteMetadataAndOrdering()
    {
        var path = CreateDatabasePath("metadata");
        const string schema =
            """
            CREATE TABLE terms(
                tenant TEXT COLLATE NOCASE,
                sequence TEXT,
                code TEXT,
                normalized TEXT GENERATED ALWAYS AS (lower(code)) VIRTUAL,
                CONSTRAINT terms_pk PRIMARY KEY(
                    tenant COLLATE NOCASE DESC,
                    sequence COLLATE RTRIM ASC
                ),
                CONSTRAINT terms_uq UNIQUE(
                    normalized COLLATE NOCASE DESC,
                    sequence COLLATE RTRIM ASC
                ) ON CONFLICT ABORT
            );
            CREATE UNIQUE INDEX terms_explicit ON terms(
                code COLLATE NOCASE DESC,
                sequence COLLATE RTRIM ASC
            );
            CREATE INDEX terms_tenant_desc ON terms(tenant COLLATE NOCASE DESC);
            """;
        const string rows =
            """
            INSERT INTO terms(rowid, tenant, sequence, code) VALUES
                (11, 'alpha', '01 ', 'Zulu'),
                (12, 'BETA', '02', 'alpha'),
                (13, NULL, '03 ', 'Echo'),
                (14, NULL, '04', 'echo2'),
                (15, 'ALPHA', '05', 'other');
            """;
        const string ordered =
            """
            SELECT rowid
            FROM terms
            ORDER BY tenant COLLATE NOCASE DESC, sequence COLLATE RTRIM ASC;
            """;

        try
        {
            long[] managedOrder;
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, schema);
                Execute(connection, rows);
                managedOrder = Query(connection, ordered)
                    .Select(row => row[0].AsInteger())
                    .ToArray();

                Action duplicateExplicit = () => Execute(
                    connection,
                    "INSERT INTO terms(rowid, tenant, sequence, code) VALUES (16, 'gamma', '01', 'ZULU');");
                duplicateExplicit.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");
                Action duplicatePrimaryKey = () => Execute(
                    connection,
                    "INSERT INTO terms(rowid, tenant, sequence, code) VALUES (17, 'ALPHA', '01', 'different');");
                duplicatePrimaryKey.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(connection, ordered)
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(managedOrder);
                Query(connection, "PRAGMA index_list(terms);")
                    .Select(row => (Name: row[1].AsText(), Origin: row[3].AsText()))
                    .Should().BeEquivalentTo(
                        [
                            ("terms_explicit", "c"),
                            ("terms_tenant_desc", "c"),
                            ("sqlite_autoindex_terms_1", "pk"),
                            ("sqlite_autoindex_terms_2", "u"),
                        ]);
                Query(connection, "PRAGMA index_info(terms_explicit);")
                    .Select(row => row[2].AsText())
                    .Should().Equal("code", "sequence");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(sqlite, ordered).Should().Equal(managedOrder);
            QueryIntegers(
                    sqlite,
                    "SELECT rowid FROM terms INDEXED BY terms_explicit ORDER BY code COLLATE NOCASE DESC, sequence COLLATE RTRIM ASC, rowid;")
            .Should().HaveCount(5);
            QueryIntegers(
                    sqlite,
                    """
                    SELECT rowid
                    FROM terms INDEXED BY terms_tenant_desc
                    WHERE tenant COLLATE NOCASE = 'alpha'
                    ORDER BY tenant COLLATE NOCASE DESC, rowid ASC;
                    """)
                .Should().Equal(11, 15);

            using (var metadata = sqlite.CreateCommand())
            {
                metadata.CommandText = "PRAGMA index_xinfo('terms_explicit');";
                using var reader = metadata.ExecuteReader();
                var terms = new List<(string? Name, long Descending, string Collation, long Key)>();
                while (reader.Read())
                {
                    terms.Add((
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetInt64(5)));
                }

                terms.Should().Equal(
                    ("code", 1, "NOCASE", 1),
                    ("sequence", 0, "RTRIM", 1),
                    (null, 0, "BINARY", 0));
            }

            using (var origins = sqlite.CreateCommand())
            {
                origins.CommandText = "SELECT origin FROM pragma_index_list('terms') ORDER BY origin;";
                using var reader = origins.ExecuteReader();
                var values = new List<string>();
                while (reader.Read())
                    values.Add(reader.GetString(0));
                values.Should().Equal("c", "c", "pk", "u");
            }

            using var differential = new MsData.SqliteConnection("Data Source=:memory:");
            differential.Open();
            Execute(differential, schema);
            Execute(differential, rows);
            QueryIntegers(differential, ordered).Should().Equal(managedOrder);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ColumnLevelTextPrimaryKeyPersistsAndRoundTripsThroughSqlite()
    {
        var path = CreateDatabasePath("column-text-pk");
        const string schema = "CREATE TABLE accounts(id TEXT PRIMARY KEY, balance INTEGER NOT NULL DEFAULT 0);";
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, schema);
                Execute(connection, "INSERT INTO accounts VALUES ('alpha', 1), ('beta', 2), ('gamma', 3);");

                Action duplicate = () => Execute(connection, "INSERT INTO accounts VALUES ('beta', 9);");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");

                Execute(connection, "INSERT OR REPLACE INTO accounts VALUES ('beta', 20);");
                Query(connection, "SELECT balance FROM accounts WHERE id = 'beta';")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(20);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(connection, "PRAGMA index_list(accounts);")
                    .Select(row => (Name: row[1].AsText(), Origin: row[3].AsText()))
                    .Should().BeEquivalentTo([("sqlite_autoindex_accounts_1", "pk")]);
                Query(connection, "SELECT id FROM accounts ORDER BY id;")
                    .Select(row => row[0].AsText())
                    .Should().Equal("alpha", "beta", "gamma");
                Action duplicate = () => Execute(connection, "INSERT INTO accounts VALUES ('alpha', 5);");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(
                    sqlite,
                    "SELECT balance FROM accounts INDEXED BY sqlite_autoindex_accounts_1 WHERE id = 'beta';")
                .Should().Equal(20);

            using (var violation = sqlite.CreateCommand())
            {
                violation.CommandText = "INSERT INTO accounts VALUES ('gamma', 30);";
                Action sqliteDuplicate = () => violation.ExecuteNonQuery();
                sqliteDuplicate.Should().Throw<MsData.SqliteException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            using (var metadata = sqlite.CreateCommand())
            {
                metadata.CommandText = "PRAGMA index_xinfo('sqlite_autoindex_accounts_1');";
                using var reader = metadata.ExecuteReader();
                var terms = new List<(string? Name, long Descending, string Collation, long Key)>();
                while (reader.Read())
                {
                    terms.Add((
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetInt64(5)));
                }

                terms.Should().Equal(
                    ("id", 0, "BINARY", 1),
                    (null, 0, "BINARY", 0));
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ColumnLevelIntegerPrimaryKeyDescPersistsAsImplicitUniqueIndex()
    {
        var path = CreateDatabasePath("integer-pk-desc");
        const string schema = "CREATE TABLE events(id INTEGER PRIMARY KEY DESC, payload TEXT);";
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, schema);
                Execute(connection, "INSERT INTO events VALUES (3, 'c'), (1, 'a'), (2, 'b');");

                Action duplicate = () => Execute(connection, "INSERT INTO events VALUES (2, 'dupe');");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");

                Query(connection, "SELECT payload FROM events ORDER BY id DESC;")
                    .Select(row => row[0].AsText())
                    .Should().Equal("c", "b", "a");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            using (var list = sqlite.CreateCommand())
            {
                list.CommandText = "SELECT name || '|' || origin FROM pragma_index_list('events');";
                using var reader = list.ExecuteReader();
                var indexes = new List<string>();
                while (reader.Read())
                    indexes.Add(reader.GetString(0));
                indexes.Should().Equal("sqlite_autoindex_events_1|pk");
            }

            QueryIntegers(sqlite, "SELECT id FROM events INDEXED BY sqlite_autoindex_events_1 ORDER BY id DESC;")
                .Should().Equal(3, 2, 1);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void ColumnLevelPrimaryKeyAndUniqueConstraintNumberingMatchesSqlite()
    {
        var path = CreateDatabasePath("column-constraint-numbering");
        const string schema =
            """
            CREATE TABLE mixed(
                a TEXT UNIQUE,
                id TEXT PRIMARY KEY,
                b TEXT UNIQUE,
                c INTEGER
            );
            CREATE TABLE wrapped(d TEXT UNIQUE PRIMARY KEY, e INTEGER);
            """;
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, schema);
                Execute(connection, "INSERT INTO mixed VALUES ('x', 'k1', 'p', 1), ('y', 'k2', 'q', 2);");
                Execute(connection, "INSERT INTO wrapped VALUES ('w', 1);");

                Action duplicateUnique = () => Execute(connection, "INSERT INTO mixed VALUES ('x', 'k3', 'r', 3);");
                duplicateUnique.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");
                Action duplicatePrimaryKey = () => Execute(connection, "INSERT INTO mixed VALUES ('z', 'k1', 'r', 3);");
                duplicatePrimaryKey.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            using (var list = sqlite.CreateCommand())
            {
                list.CommandText =
                    "SELECT name || '|' || origin FROM pragma_index_list('mixed') ORDER BY name;";
                using var reader = list.ExecuteReader();
                var indexes = new List<string>();
                while (reader.Read())
                    indexes.Add(reader.GetString(0));
                indexes.Should().Equal(
                    "sqlite_autoindex_mixed_1|u",
                    "sqlite_autoindex_mixed_2|pk",
                    "sqlite_autoindex_mixed_3|u");
            }

            using (var list = sqlite.CreateCommand())
            {
                list.CommandText =
                    "SELECT name || '|' || origin FROM pragma_index_list('wrapped') ORDER BY name;";
                using var reader = list.ExecuteReader();
                var indexes = new List<string>();
                while (reader.Read())
                    indexes.Add(reader.GetString(0));
                indexes.Should().Equal("sqlite_autoindex_wrapped_1|pk");
            }

            QueryIntegers(sqlite, "SELECT c FROM mixed INDEXED BY sqlite_autoindex_mixed_2 WHERE id = 'k2';")
                .Should().Equal(2);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void SqliteCreatedColumnLevelPrimaryKeysOpenAndStayWritable()
    {
        var path = CreateDatabasePath("sqlite-created-column-pk");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Execute(sqlite, "CREATE TABLE accounts(id TEXT PRIMARY KEY, balance INTEGER);");
                Execute(sqlite, "INSERT INTO accounts VALUES ('alpha', 1), ('beta', 2);");
                Execute(sqlite, "CREATE TABLE events(id INTEGER PRIMARY KEY DESC, payload TEXT);");
                Execute(sqlite, "INSERT INTO events VALUES (3, 'c'), (1, 'a');");
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Query(connection, "SELECT balance FROM accounts WHERE id = 'beta';")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(2);
                Query(connection, "SELECT payload FROM events ORDER BY id DESC;")
                    .Select(row => row[0].AsText())
                    .Should().Equal("c", "a");

                Action duplicate = () => Execute(connection, "INSERT INTO accounts VALUES ('alpha', 9);");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed*");

                Execute(connection, "INSERT INTO accounts VALUES ('gamma', 3);");
                Execute(connection, "INSERT INTO events VALUES (2, 'b');");
            }

            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
                QueryIntegers(sqlite, "SELECT balance FROM accounts ORDER BY id;")
                    .Should().Equal(1, 2, 3);
                QueryIntegers(sqlite, "SELECT id FROM events ORDER BY id;")
                    .Should().Equal(1, 2, 3);
            }
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PartialExpressionAffinityCollationAndNullSemanticsMatchSqlite()
    {
        const string setup =
            """
            CREATE TABLE terms(
                id INTEGER PRIMARY KEY,
                bucket INTEGER,
                value TEXT,
                note TEXT,
                enabled INTEGER
            );
            CREATE UNIQUE INDEX terms_expr ON terms(
                (CAST(value AS INTEGER) + bucket) DESC,
                lower(note) COLLATE NOCASE ASC
            ) WHERE enabled = 1;
            INSERT INTO terms VALUES
                (1, 1, '2', 'Alpha', 1),
                (2, 2, '1', 'alpha', 0),
                (3, 3, NULL, NULL, 1),
                (4, 4, NULL, NULL, 1),
                (7, 10, '1', 'Ä', 1);
            INSERT INTO terms VALUES (5, 2, '1', 'ALPHA', 1)
            ON CONFLICT(
                (CAST(value AS INTEGER) + bucket) DESC,
                lower(note) COLLATE NOCASE
            ) WHERE enabled = 1
            DO UPDATE SET note = 'updated';
            INSERT INTO terms VALUES (6, 2, '1', 'Alpha', 1);
            """;
        const string query =
            """
            SELECT id,
                   typeof(CAST(value AS INTEGER) + bucket),
                   CAST(value AS INTEGER) + bucket,
                   lower(note),
                   enabled
            FROM terms
            ORDER BY (CAST(value AS INTEGER) + bucket) DESC NULLS LAST,
                     lower(note) COLLATE NOCASE,
                     id;
            """;

        using var managed = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        managed.Open();
        managed.ExecuteNonQuery(setup);
        using var sqlite = new MsData.SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Execute(sqlite, setup);

        ReadProviderRows(managed, query).Should().Equal(ReadProviderRows(sqlite, query));
        ReadProviderRows(managed, "PRAGMA index_xinfo(terms_expr);")
            .Should().Equal(ReadProviderRows(sqlite, "PRAGMA index_xinfo(terms_expr);"));

        Action managedConflict = () => managed.ExecuteNonQuery(
            "UPDATE terms SET note = 'alpha' WHERE id = 1;");
        Action sqliteConflict = () => Execute(sqlite, "UPDATE terms SET note = 'alpha' WHERE id = 1;");
        managedConflict.Should().Throw<Exception>().WithMessage("*UNIQUE constraint failed*");
        sqliteConflict.Should().Throw<Exception>().WithMessage("*UNIQUE constraint failed*");
    }

    [Test]
    public void PartialExpressionIndexRoundTripsAndStaysAtomicAcrossConflictAndGeneratedPaths()
    {
        var path = CreateDatabasePath("partial-expression");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    PRAGMA foreign_keys = ON;
                    CREATE TABLE parent(
                        id INTEGER PRIMARY KEY,
                        code TEXT,
                        active INTEGER,
                        normalized TEXT GENERATED ALWAYS AS (lower(code)) VIRTUAL
                    );
                    CREATE UNIQUE INDEX parent_expr ON parent(
                        (lower(code) || ':' || normalized) COLLATE NOCASE DESC
                    ) WHERE active = 1;
                    CREATE TABLE child(
                        parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE,
                        payload TEXT
                    );
                    CREATE INDEX child_expr
                    ON child(parent_id + length(payload))
                    WHERE parent_id > 0;
                    CREATE TABLE ingress(id INTEGER);
                    CREATE TRIGGER ingress_after_insert AFTER INSERT ON ingress BEGIN
                        INSERT INTO parent(id, code, active) VALUES (8, 'alpha', 1);
                    END;
                    INSERT INTO parent(id, code, active) VALUES
                        (1, 'Alpha', 1),
                        (2, 'alpha', 0),
                        (3, 'alpha', 0);
                    INSERT INTO child VALUES (1, 'owned');
                    """);
                Execute(
                    connection,
                    "INSERT OR IGNORE INTO parent(id, code, active) VALUES (7, 'ALPHA', 1);");
                ReadValue(connection, "SELECT count(*) FROM parent WHERE active = 1;")
                    .Should().Be(SqlValue.Integer(1));
                Action triggerConflict = () => Execute(connection, "INSERT INTO ingress VALUES (1);");
                triggerConflict.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed: index 'parent_expr'*");
                ReadValue(connection, "SELECT count(*) FROM ingress;").Should().Be(SqlValue.Integer(0));

                Action conflictingUpdate = () => Execute(
                    connection,
                    "UPDATE parent SET active = 1 WHERE id = 2;");
                conflictingUpdate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed: index 'parent_expr'*");
                ReadValue(connection, "SELECT active FROM parent WHERE id = 2;")
                    .Should().Be(SqlValue.Integer(0));

                Execute(
                    connection,
                    """
                    INSERT INTO parent(id, code, active) VALUES (4, 'ALPHA', 1)
                    ON CONFLICT((lower(code) || ':' || normalized) COLLATE NOCASE DESC)
                    WHERE active = 1
                    DO UPDATE SET code = excluded.code;
                    """);
                Query(connection, "SELECT id, code FROM parent WHERE active = 1;")
                    .Should().ContainSingle()
                    .Which.Should().Equal(SqlValue.Integer(1), SqlValue.Text("ALPHA"));

                Execute(
                    connection,
                    "INSERT OR REPLACE INTO parent(id, code, active) VALUES (5, 'alpha', 1);");
                ReadValue(connection, "SELECT count(*) FROM child;").Should().Be(SqlValue.Integer(0));
                Query(connection, "SELECT id FROM parent WHERE active = 1;")
                    .Should().ContainSingle()
                    .Which.Should().Equal(SqlValue.Integer(5));
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadValue(connection, "SELECT count(*) FROM parent;").Should().Be(SqlValue.Integer(3));
                Query(connection, "PRAGMA index_info(parent_expr);").Single()[1]
                    .Should().Be(SqlValue.Integer(-2));
                Query(connection, "PRAGMA index_list(parent);").Single(row => row[1].AsText() == "parent_expr")[4]
                    .Should().Be(SqlValue.Integer(1));
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(
                    sqlite,
                    """
                    SELECT id FROM parent INDEXED BY parent_expr
                    WHERE active = 1
                      AND (lower(code) || ':' || normalized) COLLATE NOCASE = 'alpha:alpha';
                    """)
                .Should().Equal(5);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void WithoutRowidPartialExpressionIndexPersistsPrimaryKeySuffixes()
    {
        var path = CreateDatabasePath("partial-expression-without-rowid");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE entry(
                        tenant TEXT COLLATE NOCASE,
                        id INTEGER,
                        value TEXT,
                        active INTEGER,
                        PRIMARY KEY(tenant DESC, id ASC)
                    ) WITHOUT ROWID;
                    CREATE UNIQUE INDEX entry_expr
                    ON entry(lower(value) COLLATE NOCASE DESC)
                    WHERE active = 1;
                    INSERT INTO entry VALUES
                        ('beta', 2, 'Alpha', 1),
                        ('alpha', 1, 'alpha', 0),
                        ('gamma', 3, NULL, 1);
                    """);

                Action duplicate = () => Execute(
                    connection,
                    "UPDATE entry SET active = 1 WHERE tenant = 'alpha';");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed: index 'entry_expr'*");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                var xinfo = Query(connection, "PRAGMA index_xinfo(entry_expr);");
                xinfo[0].Should().Equal(
                    SqlValue.Integer(0),
                    SqlValue.Integer(-2),
                    SqlValue.Null,
                    SqlValue.Integer(1),
                    SqlValue.Text("NOCASE"),
                    SqlValue.Integer(1));
                xinfo.Skip(1).Select(row => (row[2].AsText(), row[5].AsInteger()))
                    .Should().Equal(("tenant", 0L), ("id", 0L));
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(
                    sqlite,
                    """
                    SELECT id FROM entry INDEXED BY entry_expr
                    WHERE active = 1 AND lower(value) COLLATE NOCASE = 'alpha';
                    """)
                .Should().Equal(2);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PartialExpressionIndexSupportsOverflowPayloads()
    {
        var path = CreateDatabasePath("partial-expression-overflow");
        var first = new string('A', 5_000);
        var second = new string('B', 6_000);
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "PRAGMA page_size=512; VACUUM;");
                Execute(connection, "CREATE TABLE payload(id INTEGER PRIMARY KEY, value TEXT, active INTEGER);");
                Execute(
                    connection,
                    "CREATE INDEX payload_expr ON payload(lower(value) || value DESC) WHERE active = 1;");
                using (var insert = connection.Prepare("INSERT INTO payload VALUES (1, ?1, 1), (2, ?2, 0);"))
                {
                    insert.Bind(1, SqlValue.Text(first));
                    insert.Bind(2, SqlValue.Text(second));
                    insert.Step().Should().Be(StatementStepResult.Done);
                }
                using var update = connection.Prepare("UPDATE payload SET value = ?1 WHERE id = 1;");
                update.Bind(1, SqlValue.Text(second));
                update.Step().Should().Be(StatementStepResult.Done);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadValue(connection, "SELECT length(value) FROM payload WHERE id = 1;")
                    .Should().Be(SqlValue.Integer(6_000));
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Convert.ToInt64(Scalar(
                sqlite,
                "SELECT count(*) FROM payload INDEXED BY payload_expr WHERE active = 1;")).Should().Be(1);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void BitwisePartialExpressionIndexRoundTripsAndRejectsRowValuesBeforePublication()
    {
        var path = CreateDatabasePath("partial-bitwise-expression");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE items(id INTEGER PRIMARY KEY,flags INTEGER,kind INTEGER,payload TEXT);
                    INSERT INTO items VALUES (1,1,2,'one'),(2,2,3,'two'),(3,3,4,'three');
                    CREATE INDEX items_bits
                        ON items(((flags << 4) | kind) DESC)
                        WHERE (flags & 1) = 1;
                    """);

                Action rowValue = () => Execute(
                    connection,
                    "CREATE INDEX rejected_row_value ON items((flags,kind));");
                rowValue.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*expression is prohibited in index expressions*");
                Query(connection, "PRAGMA index_list(items);").Select(row => row[1].AsText())
                    .Should().Contain("items_bits").And.NotContain("rejected_row_value");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(
                        connection,
                        """
                        SELECT id FROM items
                        WHERE (flags & 1) = 1
                        ORDER BY ((flags << 4) | kind) DESC
                        """)
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(3, 1);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            ScalarText(sqlite, "SELECT sql FROM sqlite_schema WHERE name='items_bits';")
                .Should().Contain("(flags << 4) | kind")
                .And.Contain("WHERE (flags & 1) = 1");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void Utf16RTrimIndexUsesSqliteUtf8CollationOrder()
    {
        var path = CreateDatabasePath("utf16-rtrim");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Execute(
                    sqlite,
                    """
                    PRAGMA encoding='UTF-16le';
                    CREATE TABLE terms(id INTEGER PRIMARY KEY, value TEXT COLLATE RTRIM);
                    INSERT INTO terms VALUES
                        (1, 'Ā'),
                        (2, 'ÿ'),
                        (4, ''),
                        (5, '𐀀');
                    CREATE INDEX terms_rtrim ON terms(value);
                    """);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "INSERT INTO terms VALUES (3, 'z ');");
                Query(
                        connection,
                        "SELECT value FROM terms ORDER BY value COLLATE RTRIM;")
                    .Select(row => row[0].AsText())
                    .Should().Equal("z ", "ÿ", "Ā", "", "𐀀");
            }

            using var reopened = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            reopened.Open();
            ScalarText(reopened, "PRAGMA encoding;").Should().Be("UTF-16le");
            ScalarText(reopened, "PRAGMA integrity_check;").Should().Be("ok");
            using var command = reopened.CreateCommand();
            command.CommandText =
                "SELECT value FROM terms INDEXED BY terms_rtrim ORDER BY value COLLATE RTRIM;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
                values.Add(reader.GetString(0));
            values.Should().Equal("z ", "ÿ", "Ā", "", "𐀀");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void OpensAndMutatesSqliteCreatedPartialExpressionIndexes()
    {
        var path = CreateDatabasePath("sqlite-created-partial-expression");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Execute(
                    sqlite,
                    """
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE external(id INTEGER PRIMARY KEY, value TEXT, active INTEGER);
                    CREATE UNIQUE INDEX external_expr
                    ON external(lower(value) COLLATE NOCASE DESC)
                    WHERE active = 1;
                    INSERT INTO external VALUES
                        (1, 'Alpha', 1),
                        (2, 'alpha', 0);
                    """);
            }

            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
                Action duplicate = () => Execute(
                    connection,
                    "UPDATE external SET active = 1 WHERE id = 2;");
                duplicate.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("*UNIQUE constraint failed: index 'external_expr'*");
                Execute(connection, "UPDATE external SET value = 'Beta' WHERE id = 1;");
                Execute(connection, "UPDATE external SET active = 1 WHERE id = 2;");
            }

            using var reopened = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            reopened.Open();
            ScalarText(reopened, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(
                    reopened,
                    """
                    SELECT id FROM external INDEXED BY external_expr
                    WHERE active = 1
                    ORDER BY lower(value) COLLATE NOCASE DESC;
                    """)
                .Should().Equal(1, 2);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void NoCaseEmbeddedNullUsesSqliteTerminatorAndUniqueSemantics()
    {
        var path = CreateDatabasePath("nocase-null");
        try
        {
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Execute(
                    sqlite,
                    """
                    CREATE TABLE terms(id INTEGER PRIMARY KEY, value TEXT);
                    CREATE INDEX terms_nocase ON terms(value COLLATE NOCASE);
                    """);
                using var insert = sqlite.CreateCommand();
                insert.CommandText = "INSERT INTO terms VALUES ($id, $value);";
                var id = insert.Parameters.Add("$id", MsData.SqliteType.Integer);
                var value = insert.Parameters.Add("$value", MsData.SqliteType.Text);
                id.Value = 1L;
                value.Value = "a\0c";
                insert.ExecuteNonQuery();
                id.Value = 2L;
                value.Value = "A\0b";
                insert.ExecuteNonQuery();
            }

            using (var managed = OpenManaged(path, pooling: false))
            {
                QueryManagedIntegers(
                        managed,
                        "SELECT id FROM terms ORDER BY value COLLATE NOCASE, id;")
                    .Should().Equal(1, 2);
                using var insert = managed.CreateCommand();
                insert.CommandText = "INSERT INTO terms VALUES ($id, $value);";
                insert.Parameters.Add("$id", SqliteType.Integer).Value = 3L;
                insert.Parameters.Add("$value", SqliteType.Text).Value = "a\0a";
                insert.ExecuteNonQuery();

                managed.ExecuteNonQuery(
                    """
                    CREATE TABLE unique_terms(value TEXT);
                    CREATE UNIQUE INDEX unique_terms_nocase
                        ON unique_terms(value COLLATE NOCASE);
                    """);
                using var uniqueInsert = managed.CreateCommand();
                uniqueInsert.CommandText = "INSERT INTO unique_terms VALUES ($value);";
                var uniqueValue = uniqueInsert.Parameters.Add("$value", SqliteType.Text);
                uniqueValue.Value = "a\0c";
                uniqueInsert.ExecuteNonQuery();
                uniqueValue.Value = "A\0b";
                Action duplicate = () => uniqueInsert.ExecuteNonQuery();
                duplicate.Should().Throw<SqliteException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            using var reopened = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            reopened.Open();
            ScalarText(reopened, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(
                    reopened,
                    "SELECT id FROM terms INDEXED BY terms_nocase ORDER BY value COLLATE NOCASE, id;")
                .Should().Equal(1, 2, 3);
            Convert.ToInt64(Scalar(reopened, "SELECT COUNT(*) FROM unique_terms;"))
                .Should().Be(1);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void RichIndexRewritesRebalanceAtSmallPagePressureAndSurviveMigration()
    {
        var path = CreateDatabasePath("pressure");
        uint rootPage;
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, bucket TEXT, padded TEXT);");
                Execute(connection, BuildPressureRows(1, 1_400));
                Execute(
                    connection,
                    "CREATE INDEX items_order ON items(bucket COLLATE NOCASE DESC, padded COLLATE RTRIM ASC);");
                Execute(
                    connection,
                    "CREATE INDEX items_expr ON items(lower(bucket) || ':' || padded) WHERE (id % 2) = 0;");
                ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
                Execute(connection, "PRAGMA page_size=512; VACUUM;");
                Execute(
                    connection,
                    "UPDATE items SET bucket = 'rotated-' || (id % 23), padded = 'changed-' || id || '   ' WHERE id <= 300;");
                Execute(connection, "DELETE FROM items WHERE id > 600 AND id <= 800;");
                ReadValue(connection, "SELECT COUNT(*) FROM items;").Should().Be(SqlValue.Integer(1_200));
            }

            using (var store = SqlitePageStore.Open(PhysicalFileSystem.Instance, path))
            {
                rootPage = FindIndexRootPage(store, "items_order");
                var comparer = new SqliteIndexRecordComparer(
                    store.Header.TextEncoding,
                    [true, false],
                    ["NOCASE", "RTRIM"]);
                var overflow = new SqliteOverflowChainReader(store);
                ReadIndexHeight(store, rootPage, comparer, overflow).Should().BeGreaterThanOrEqualTo(3);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                ReadValue(connection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(512));
                ReadValue(connection, "PRAGMA journal_mode;").Should().Be(SqlValue.Text("delete"));
                ReadValue(connection, "SELECT COUNT(*) FROM items;").Should().Be(SqlValue.Integer(1_200));
                ReadValue(connection, "SELECT bucket FROM items WHERE id = 17;")
                    .Should().Be(SqlValue.Text("rotated-17"));
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Convert.ToInt64(Scalar(sqlite, "PRAGMA page_size;")).Should().Be(512);
            Convert.ToInt64(Scalar(
                sqlite,
                "SELECT COUNT(*) FROM items INDEXED BY items_order;")).Should().Be(1_200);
            Convert.ToInt64(Scalar(
                sqlite,
                "SELECT COUNT(*) FROM items INDEXED BY items_expr WHERE (id % 2) = 0;")).Should().Be(600);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void LimitedDmlNullPlacementPreservesRichIndexOrdering()
    {
        var path = CreateDatabasePath("limited-dml-null-order");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE ranked(id INTEGER PRIMARY KEY, label TEXT, marker TEXT);
                    INSERT INTO ranked VALUES
                        (1, 'b', NULL),
                        (2, 'A', NULL),
                        (3, NULL, NULL),
                        (4, 'a', NULL),
                        (5, NULL, NULL);
                    CREATE INDEX ranked_label
                        ON ranked(label COLLATE NOCASE DESC);
                    """);

                Query(
                        connection,
                        """
                        UPDATE ranked SET marker = 'updated'
                        RETURNING id
                        ORDER BY label COLLATE NOCASE DESC NULLS FIRST, id ASC
                        LIMIT 2;
                        """)
                    .Select(row => row[0].AsInteger())
                    .Order()
                    .Should().Equal(3, 5);
                Query(
                        connection,
                        """
                        DELETE FROM ranked
                        RETURNING id
                        ORDER BY label COLLATE NOCASE ASC NULLS LAST, id DESC
                        LIMIT 1;
                        """)
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(4);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                Query(connection, "SELECT id FROM ranked WHERE marker = 'updated' ORDER BY id;")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(3, 5);
                Query(connection, "SELECT id FROM ranked ORDER BY id;")
                    .Select(row => row[0].AsInteger())
                    .Should().Equal(1, 2, 3, 5);
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            QueryIntegers(
                    sqlite,
                    """
                    SELECT id FROM ranked INDEXED BY ranked_label
                    ORDER BY label COLLATE NOCASE DESC, id ASC;
                    """)
                .Should().Equal(1, 2, 3, 5);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void LimitedRowAssignmentsKeepPartialExpressionIndexesAtomic()
    {
        var path = CreateDatabasePath("limited-dml-partial-row-assignment");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE items(
                        id INTEGER PRIMARY KEY,
                        active INTEGER,
                        left_value INTEGER,
                        right_value INTEGER
                    );
                    INSERT INTO items VALUES (1,1,1,2),(2,1,3,4),(3,0,1,2);
                    CREATE UNIQUE INDEX items_active_key
                        ON items((left_value << 8) | right_value)
                        WHERE active = 1;
                    UPDATE items
                    SET (left_value,right_value)=(right_value,left_value)
                    WHERE active = 1
                    ORDER BY id
                    LIMIT 2;
                    """);

                Query(connection, "SELECT id,left_value,right_value FROM items ORDER BY id;")
                    .Select(row => string.Join(':', row.Select(value => value.AsInteger())))
                    .Should().Equal("1:2:1", "2:4:3", "3:1:2");

                Action conflict = () => Execute(
                    connection,
                    """
                    UPDATE items
                    SET (left_value,right_value)=(9,9)
                    WHERE active = 1
                    ORDER BY id
                    LIMIT 2;
                    """);
                conflict.Should().Throw<EmbeddedSqlException>()
                    .WithMessage("UNIQUE constraint failed: index 'items_active_key'");
                Query(connection, "SELECT id,left_value,right_value FROM items ORDER BY id;")
                    .Select(row => string.Join(':', row.Select(value => value.AsInteger())))
                    .Should().Equal("1:2:1", "2:4:3", "3:1:2");
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Convert.ToInt64(Scalar(
                    sqlite,
                    "SELECT count(*) FROM items INDEXED BY items_active_key WHERE active = 1;"))
                .Should().Be(2);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedRichIndexRewriteRecoversAtomicallyInWalAndDeleteModes(bool deleteMode)
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        var path = deleteMode ? "rich-index-delete-fault.db" : "rich-index-wal-fault.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildSimpleRows(1, 120));
            Execute(connection, "CREATE UNIQUE INDEX items_value ON items(value COLLATE NOCASE DESC);");
            Execute(
                connection,
                "CREATE UNIQUE INDEX items_expr ON items(lower(value) || ':' || id) WHERE id <= 80;");
            if (deleteMode)
                ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));

            faults.FailNext(FileSystemOperation.Write);
            Assert.Throws<IOException>(() =>
                Execute(connection, "UPDATE items SET value = 'changed-' || id WHERE id <= 40;"));
        }

        faults.ClearScheduled();
        using (var recovered = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = recovered.Connect())
        {
            ReadValue(connection, "SELECT value FROM items WHERE id = 17;")
                .Should().Be(SqlValue.Text("value-00017"));
            Query(connection, "PRAGMA index_list(items);")
                .Select(row => row[1].AsText())
                .Should().Contain(["items_value", "items_expr"]);
            Execute(connection, "UPDATE items SET value = 'changed-' || id WHERE id <= 40;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "SELECT value FROM items WHERE id = 17;")
            .Should().Be(SqlValue.Text("changed-17"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EncryptedRichIndexesReopenInWalAndDeleteModes(bool deleteMode)
    {
        using var encryption = AhtolaEncryptionOptions.FromHex(
            StorageCipher.Aes256Gcm,
            Aes256Key);
        var fileSystem = new AhtolaEncryptionFileSystem(new InMemoryFileSystem(), encryption);
        var path = deleteMode ? "encrypted-index-delete.db" : "encrypted-index-wal.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, BuildSimpleRows(1, 80));
            Execute(connection, "CREATE INDEX items_value ON items(value COLLATE RTRIM DESC);");
            Execute(
                connection,
                "CREATE INDEX items_expr ON items(lower(value) || ':' || id) WHERE (id % 2) = 0;");
            if (deleteMode)
                ReadValue(connection, "PRAGMA journal_mode=DELETE;").Should().Be(SqlValue.Text("delete"));
            Execute(connection, "UPDATE items SET value = value || ' ' WHERE id <= 20;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var reopenedConnection = reopened.Connect();
        ReadValue(reopenedConnection, "SELECT COUNT(*) FROM items;").Should().Be(SqlValue.Integer(80));
        Query(reopenedConnection, "PRAGMA index_list(items);")
            .Select(row => row[1].AsText())
            .Should().Contain(["items_value", "items_expr"]);
        ReadValue(
                reopenedConnection,
                "SELECT COUNT(*) FROM items WHERE (id % 2) = 0 AND lower(value) || ':' || id IS NOT NULL;")
            .Should().Be(SqlValue.Integer(40));
    }

    [Test]
    public void BackupAttachAndPooledRefreshPreserveRichIndexSemantics()
    {
        var sourcePath = CreateDatabasePath("pooled-source");
        var destinationPath = CreateDatabasePath("backup");
        var mainPath = CreateDatabasePath("attach-main");
        SqliteConnection.ClearAllPools();
        try
        {
            using var writer = OpenManaged(sourcePath, pooling: true);
            using var stale = OpenManaged(sourcePath, pooling: true);
            var stalePhysical = stale.ManagedConnection;
            stale.Close();

            writer.ExecuteNonQuery(
                """
                CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT, suffix TEXT);
                CREATE UNIQUE INDEX items_value ON items(value COLLATE NOCASE DESC, suffix COLLATE RTRIM ASC);
                CREATE INDEX items_expr ON items(lower(value) || ':' || length(suffix)) WHERE id > 0;
                INSERT INTO items VALUES (1, 'Alpha', 'x ');
                """);
            writer.Close();

            using (var current = OpenManaged(sourcePath, pooling: true))
            {
                stale.Open();
                stale.ManagedConnection.Should().BeSameAs(stalePhysical);
                stale.ExecuteScalar<string>(
                    "SELECT sql FROM sqlite_master WHERE name = 'items_value';")
                    .Should().Contain("COLLATE NOCASE DESC").And.Contain("COLLATE RTRIM");
                stale.ExecuteScalar<string>(
                    "SELECT sql FROM sqlite_master WHERE name = 'items_expr';")
                    .Should().Contain("lower(value)").And.Contain("WHERE id > 0");
            }

            using (var destination = OpenManaged(destinationPath, pooling: false))
                stale.BackupDatabase(destination);
            stale.Close();

            using (var reopened = OpenManaged(destinationPath, pooling: false))
            {
                Action duplicate = () => reopened.ExecuteNonQuery(
                    "INSERT INTO items VALUES (2, 'ALPHA', 'x');");
                duplicate.Should().Throw<SqliteException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            using (var main = OpenManaged(mainPath, pooling: false))
            {
                main.ExecuteNonQuery(
                    $"ATTACH DATABASE '{EscapeSqlLiteral(destinationPath)}' AS aux;");
                main.ExecuteScalar<string>(
                    "SELECT sql FROM aux.sqlite_master WHERE name = 'items_value';")
                    .Should().Contain("COLLATE NOCASE DESC");
                main.ExecuteScalar<long>(
                    """
                    SELECT count(*) FROM aux.items
                    WHERE id > 0 AND lower(value) || ':' || length(suffix) = 'alpha:2';
                    """).Should().Be(1);
                Action duplicate = () => main.ExecuteNonQuery(
                    "INSERT INTO aux.items VALUES (3, 'alpha', 'x   ');");
                duplicate.Should().Throw<SqliteException>()
                    .WithMessage("*UNIQUE constraint failed*");
                main.ExecuteNonQuery("DETACH aux;");
            }

            using var sqlite = new MsData.SqliteConnection(
                $"Data Source={destinationPath};Pooling=False");
            sqlite.Open();
            ScalarText(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Convert.ToInt64(Scalar(
                sqlite,
                """
                SELECT count(*) FROM items INDEXED BY items_expr
                WHERE id > 0 AND lower(value) || ':' || length(suffix) = 'alpha:2';
                """)).Should().Be(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(sourcePath);
            DeleteDatabase(destinationPath);
            DeleteDatabase(mainPath);
        }
    }

    [Test]
    public void CustomCollationRejectsBeforePublicationAndRichOrderCorruptionFailsReopen()
    {
        const string rejectedPath = "custom-index-reject.db";
        var faults = new DeterministicFaultInjector();
        var rejectedFileSystem = new InMemoryFileSystem(faults);
        using (var database = EmbeddedDatabase.OpenFile(rejectedPath, rejectedFileSystem))
        using (var connection = database.Connect())
        {
            database.RegisterCollation(
                "reverse_text",
                (left, right) => string.CompareOrdinal(right, left));
            database.RegisterScalarFunction("managed_index_value", 1, values => values[0]);
            database.RegisterScalarFunction("lower", 1, values => values[0]);
            Execute(connection, "CREATE TABLE retained(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO retained VALUES (1, 'durable');");
            var writesBeforeReject = faults.GetOperationCount(FileSystemOperation.Write);
            faults.FailNext(FileSystemOperation.Write);

            Action random = () => Execute(
                connection,
                "CREATE INDEX rejected_random ON retained(random());");
            random.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*non-deterministic functions*");
            Action udf = () => Execute(
                connection,
                "CREATE INDEX rejected_udf ON retained(managed_index_value(value));");
            udf.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*non-deterministic functions*");
            Action overrideBuiltin = () => Execute(
                connection,
                "CREATE INDEX rejected_override ON retained(lower(value));");
            overrideBuiltin.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*application-defined functions are prohibited*");
            Action create = () => Execute(
                connection,
                "CREATE INDEX rejected_custom ON retained(value COLLATE reverse_text);");
            create.Should().Throw<EmbeddedSqlException>()
                .WithMessage("*not a supported SQLite built-in collation*");
            faults.GetOperationCount(FileSystemOperation.Write).Should().Be(writesBeforeReject);
            Query(connection, "PRAGMA index_list(retained);").Should().BeEmpty();
            faults.ClearScheduled();
        }

        using (var reopened = EmbeddedDatabase.OpenFile(rejectedPath, rejectedFileSystem))
        using (var connection = reopened.Connect())
            ReadValue(connection, "SELECT value FROM retained WHERE id = 1;")
                .Should().Be(SqlValue.Text("durable"));

        const string corruptPath = "rich-index-corrupt-order.db";
        var corruptFileSystem = new InMemoryFileSystem();
        uint rootPage;
        SqliteDatabaseHeader header;
        using (var database = EmbeddedDatabase.OpenFile(corruptPath, corruptFileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE items(id INTEGER PRIMARY KEY, value TEXT);");
            Execute(connection, "INSERT INTO items VALUES (1, 'a'), (2, 'B'), (3, 'c'), (4, 'D');");
            Execute(connection, "CREATE INDEX items_value ON items(value COLLATE NOCASE DESC);");
        }

        using (var store = SqlitePageStore.Open(corruptFileSystem, corruptPath))
        {
            header = store.Header;
            rootPage = FindIndexRootPage(store, "items_value");
            var page = store.ReadPage(rootPage);
            var pageHeader = SqliteBtreePageHeader.Parse(page);
            pageHeader.PageType.Should().Be(SqliteBtreePageType.IndexLeaf);
            pageHeader.CellCount.Should().BeGreaterThanOrEqualTo(2);
            var firstPointer = pageHeader.CellPointerArrayOffset;
            var first = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(firstPointer));
            var second = BinaryPrimitives.ReadUInt16BigEndian(
                page.AsSpan(firstPointer + sizeof(ushort)));
            BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(firstPointer), second);
            BinaryPrimitives.WriteUInt16BigEndian(
                page.AsSpan(firstPointer + sizeof(ushort)),
                first);
            store.WritePage(rootPage, page);
            store.Flush();
        }

        ReplaceWalWithEmptyFile(corruptFileSystem, corruptPath, header);
        Action reopenCorrupt = () => EmbeddedDatabase.OpenFile(corruptPath, corruptFileSystem);
        reopenCorrupt.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*not a valid supported SQLite index b-tree*");
    }

    [Test]
    public void IndexExpressionAcceptsDeterministicStrftimeWithColumn()
    {
        // strftime(format, column) is deterministic (no 'now' time value, no
        // localtime/utc modifiers) and is therefore permitted in an index
        // expression, mirroring SQLite/Turso.
        using var database = EmbeddedDatabase.OpenFile(
            CreateDatabasePath("idx-strftime-column"),
            new InMemoryFileSystem());
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE logs(\"created\" TEXT NOT NULL);");
        Execute(
            connection,
            "CREATE INDEX idx_logs_created_hour ON logs(strftime('%Y-%m-%d %H:00:00', \"created\"));");
        Execute(connection, "INSERT INTO logs VALUES ('2024-01-01 12:34:56');");
        ReadValue(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND name = 'idx_logs_created_hour';")
            .Should().Be(SqlValue.Text("idx_logs_created_hour"));
    }

    [Test]
    public void IndexExpressionAcceptsDeterministicDateLiteral()
    {
        // date('2020-01-02') is a fully literal, deterministic call and is
        // permitted in an index expression.
        using var database = EmbeddedDatabase.OpenFile(
            CreateDatabasePath("idx-date-literal"),
            new InMemoryFileSystem());
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE logs(\"created\" TEXT NOT NULL);");
        Execute(
            connection,
            "CREATE INDEX idx_logs_date_literal ON logs(date('2020-01-02'));");
        Execute(connection, "INSERT INTO logs VALUES ('2024-01-01 12:34:56');");
        ReadValue(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND name = 'idx_logs_date_literal';")
            .Should().Be(SqlValue.Text("idx_logs_date_literal"));
    }

    [Test]
    public void IndexExpressionRejectsStrftimeWithNowTimeValue()
    {
        // strftime(format, 'now') reads the wall clock and remains
        // non-deterministic, so it is rejected for index expressions.
        using var database = EmbeddedDatabase.OpenFile(
            CreateDatabasePath("idx-strftime-now"),
            new InMemoryFileSystem());
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE logs(\"created\" TEXT NOT NULL);");
        Action create = () => Execute(
            connection,
            "CREATE INDEX idx_logs_created_hour ON logs(strftime('%Y-%m-%d %H:00:00', 'now'));");
        create.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*non-deterministic functions*");
    }

    private static int ReadIndexHeight(
        SqlitePageStore store,
        uint pageNumber,
        SqliteIndexRecordComparer comparer,
        SqliteOverflowChainReader overflow)
    {
        var page = store.ReadPage(pageNumber);
        var header = SqliteBtreePageHeader.Parse(page);
        if (header.PageType == SqliteBtreePageType.IndexLeaf)
        {
            SqliteIndexLeafPageView.Parse(
                page,
                store.Header.UsableSpace,
                store.Header.TextEncoding,
                overflowReader: overflow,
                recordComparer: comparer);
            return 0;
        }

        var interior = SqliteIndexInteriorPageView.Parse(
            page,
            store.Header.UsableSpace,
            store.Header.TextEncoding,
            overflowReader: overflow,
            recordComparer: comparer);
        var heights = interior.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(interior.Header.RightMostChildPage)
            .Select(child => ReadIndexHeight(store, child, comparer, overflow))
            .Distinct()
            .ToArray();
        heights.Should().ContainSingle();
        return checked(heights[0] + 1);
    }

    private static uint FindIndexRootPage(SqlitePageStore store, string indexName)
    {
        var schema = SqliteTableLeafPageView.Parse(
            store.ReadPage(1),
            store.Header.UsableSpace,
            isFirstPage: true);
        return checked((uint)schema.Cells
            .Select(cell => SqliteRecordCodec.Decode(
                cell.Cell.LocalPayload.Span,
                store.Header.TextEncoding))
            .Single(values =>
                values[0].AsText() == "index"
                && values[1].AsText() == indexName)[3]
            .AsInteger());
    }

    private static string BuildPressureRows(int firstId, int count)
    {
        var rows = Enumerable.Range(firstId, count)
            .Select(id =>
                $"({id}, 'bucket-{id % 17:D2}', 'value-{id:D5}-{new string('x', 58)}   ')");
        return $"INSERT INTO items VALUES {string.Join(", ", rows)};";
    }

    private static string BuildSimpleRows(int firstId, int count)
    {
        var rows = Enumerable.Range(firstId, count)
            .Select(id => $"({id}, 'value-{id:D5}')");
        return $"INSERT INTO items VALUES {string.Join(", ", rows)};";
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
                statement.Step().Should().Be(StatementStepResult.Done);
        }
    }

    private static void Execute(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
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

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
        => Query(connection, sql).Single().Single();

    private static long[] QueryIntegers(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
            values.Add(reader.GetInt64(0));
        return values.ToArray();
    }

    private static long[] QueryManagedIntegers(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
            values.Add(reader.GetInt64(0));
        return values.ToArray();
    }

    private static string[] ReadProviderRows(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.IsDBNull(index)
                        ? "<null>"
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)
                            ?? string.Empty)));
        }

        return rows.ToArray();
    }

    private static object? Scalar(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ScalarText(MsData.SqliteConnection connection, string sql)
        => (string)Scalar(connection, sql)!;

    private static SqliteConnection OpenManaged(string path, bool pooling)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Pooling={pooling};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void ReplaceWalWithEmptyFile(
        IFileSystem fileSystem,
        string path,
        SqliteDatabaseHeader header)
    {
        fileSystem.DeleteFile(path + "-wal");
        using var wal = SqliteWalFile.Create(
            fileSystem,
            path + "-wal",
            SqliteWalHeader.Create(header.PageSize, salt1: 101, salt2: 103));
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CreateDatabasePath(string suffix)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "durable-index-semantics");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{suffix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
