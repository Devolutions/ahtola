using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedAlterTableDropColumnTests
{
    private const string Aes256Key =
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Test]
    public void DropColumnMatchesMicrosoftDataSqliteAndPreservesRichRetainedSchema()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        const string setup = """
            CREATE TABLE data(
                id INTEGER PRIMARY KEY,
                keep TEXT COLLATE NOCASE CONSTRAINT keep_default DEFAULT 'fallback',
                removed BLOB,
                score INTEGER CONSTRAINT positive_score CHECK(score > 0),
                doubled INTEGER GENERATED ALWAYS AS (score * 2) VIRTUAL,
                CONSTRAINT unique_keep UNIQUE(keep)
            );
            CREATE INDEX data_score_desc ON data(score COLLATE NOCASE DESC);
            INSERT INTO data(id, keep, removed, score) VALUES (41, 'value', X'0102', 7);
            CREATE TABLE audit(events INTEGER);
            INSERT INTO audit VALUES (0);
            CREATE VIEW data_view AS SELECT id, keep, score, doubled FROM data;
            CREATE VIEW data_rowid_view AS SELECT rowid, keep FROM data;
            CREATE VIEW data_star_view(
                id_value, keep_value, removed_value, score_value, doubled_value
            ) AS SELECT * FROM data;
            CREATE VIEW nested_star_view AS SELECT removed_value FROM data_star_view;
            CREATE TRIGGER data_insert AFTER INSERT ON data
            BEGIN UPDATE audit SET events = events + 1; END;
            """;

        Execute(managed, setup);
        Execute(sqlite, setup);
        Execute(managed, "ALTER TABLE data DROP COLUMN removed;");
        Execute(sqlite, "ALTER TABLE data DROP COLUMN removed;");
        Execute(managed, "INSERT INTO data(id, keep, score) VALUES (42, 'second', 3);");
        Execute(sqlite, "INSERT INTO data(id, keep, score) VALUES (42, 'second', 3);");

        ReadRows(managed, "SELECT rowid, id, keep, score, doubled FROM data;")
            .Should().Equal(ReadRows(sqlite, "SELECT rowid, id, keep, score, doubled FROM data;"));
        ReadRows(managed, "SELECT * FROM data_view ORDER BY id;")
            .Should().Equal(ReadRows(sqlite, "SELECT * FROM data_view ORDER BY id;"));
        ReadRows(managed, "SELECT * FROM data_rowid_view;")
            .Should().BeEquivalentTo(ReadRows(sqlite, "SELECT * FROM data_rowid_view;"));
        Scalar<long>(managed, "SELECT events FROM audit;").Should().Be(1);
        Scalar<long>(sqlite, "SELECT events FROM audit;").Should().Be(1);
        ReadRows(managed, "PRAGMA table_xinfo(data);")
            .Should().Equal(ReadRows(sqlite, "PRAGMA table_xinfo(data);"));
        ReadRows(managed, "PRAGMA index_info(data_score_desc);")
            .Should().Equal(ReadRows(sqlite, "PRAGMA index_info(data_score_desc);"));

        // SQLite keeps the original CREATE text and edits out only the dropped definition
        // (sqlite3AlterDropColumn), so the stored sql must match byte for byte.
        Scalar<string>(
                managed,
                "SELECT sql FROM sqlite_schema WHERE type='table' AND name='data';")
            .Should().Be(Scalar<string>(sqlite, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='data';"))
            .And.Contain("keep TEXT COLLATE NOCASE CONSTRAINT keep_default DEFAULT 'fallback'")
            .And.Contain("doubled INTEGER GENERATED ALWAYS AS (score * 2) VIRTUAL")
            .And.Contain("CONSTRAINT unique_keep UNIQUE(keep)")
            .And.NotContain("removed");
        Scalar<string>(
                managed,
                "SELECT sql FROM sqlite_schema WHERE type='index' AND name='data_score_desc';")
            .Should().Be(Scalar<string>(sqlite, "SELECT sql FROM sqlite_schema WHERE type='index' AND name='data_score_desc';"))
            .And.Be("CREATE INDEX data_score_desc ON data(score COLLATE NOCASE DESC)");
        Scalar<string>(managed, "SELECT sql FROM sqlite_schema WHERE name='data_view';")
            .Should().Contain("SELECT id, keep, score, doubled FROM data");
        Scalar<string>(managed, "SELECT sql FROM sqlite_schema WHERE name='data_star_view';")
            .Should().Contain("AS SELECT * FROM data");
        Scalar<string>(managed, "SELECT sql FROM sqlite_schema WHERE name='nested_star_view';")
            .Should().Contain("SELECT removed_value FROM data_star_view");
        Scalar<string>(managed, "SELECT sql FROM sqlite_schema WHERE name='data_insert';")
            .Should().Contain("UPDATE audit SET events = events + 1");
        ((Action)(() => ReadRows(managed, "SELECT * FROM data_star_view;")))
            .Should().Throw<Exception>().WithMessage("*expected 5 columns*got 4*");
        ((Action)(() => ReadRows(sqlite, "SELECT * FROM data_star_view;")))
            .Should().Throw<Exception>().WithMessage("*expected 5 columns*got 4*");
    }

    [Test]
    public void DropColumnPreservesAutoincrementStrictAndPartialExpressionIndexState()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var connection = OpenManagedFile(path))
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE data(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        removed TEXT,
                        flags INT,
                        kind INT
                    ) STRICT;
                    INSERT INTO data(id,removed,flags,kind) VALUES
                        (1,'drop-me',1,2),
                        (20,'high',3,4);
                    DELETE FROM data WHERE id=20;
                    CREATE INDEX data_bits
                        ON data((flags << 4) | kind)
                        WHERE (flags & 1) = 1;
                    ALTER TABLE data DROP COLUMN removed;
                    INSERT INTO data(flags,kind) VALUES (5,6);
                    """);

                Scalar<long>(connection, "SELECT id FROM data WHERE flags=5;").Should().Be(21);
                Scalar<long>(
                        connection,
                        "SELECT seq FROM sqlite_sequence WHERE name='data';")
                    .Should().Be(21);
                Scalar<string>(
                        connection,
                        "SELECT sql FROM sqlite_schema WHERE name='data';")
                    .Should().EndWith(" STRICT");
                Scalar<string>(
                        connection,
                        "SELECT sql FROM sqlite_schema WHERE name='data_bits';")
                    .Should().Contain("(flags << 4) | kind")
                    .And.Contain("WHERE (flags & 1) = 1");
            }

            ManagedSqliteConnection.ClearAllPools();
            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            Scalar<string>(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Scalar<long>(sqlite, "SELECT seq FROM sqlite_sequence WHERE name='data';").Should().Be(21);
            Scalar<long>(
                    sqlite,
                    "SELECT count(*) FROM data INDEXED BY data_bits WHERE (flags & 1) = 1;")
                .Should().Be(2);
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void DropColumnPreservesRetainedDeterministicDateExpressionIndex()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        const string setup = """
            CREATE TABLE t(a INTEGER, b INTEGER, c TEXT, d INTEGER);
            CREATE INDEX i_expr ON t(a, date(c), c);
            INSERT INTO t VALUES(1, 2, '2026-01-01', 3);
            """;

        Execute(managed, setup);
        Execute(sqlite, setup);
        Execute(managed, "ALTER TABLE t DROP COLUMN b; UPDATE t SET a = 5 WHERE c = '2026-01-01';");
        Execute(sqlite, "ALTER TABLE t DROP COLUMN b; UPDATE t SET a = 5 WHERE c = '2026-01-01';");

        ReadRows(managed, "SELECT a, c, d FROM t;")
            .Should()
            .Equal(ReadRows(sqlite, "SELECT a, c, d FROM t;"));
    }

    [Test]
    public void AddColumnRejectsUnknownCollation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(c1 INTEGER);");

        Assert.Throws<EmbeddedSqlException>(
                () => Execute(connection, "ALTER TABLE t ADD COLUMN c2 INTEGER COLLATE compile_options;"))!
            .Message
            .Should()
            .Be("no such collation sequence: compile_options");
    }

    [TestCase(
        "CREATE TABLE t(a);",
        "a",
        "no other columns")]
    [TestCase(
        "CREATE TABLE t(a INTEGER PRIMARY KEY, b);",
        "a",
        "PRIMARY KEY")]
    [TestCase(
        "CREATE TABLE t(a INTEGER PRIMARY KEY);",
        "a",
        "PRIMARY KEY")]
    [TestCase(
        "CREATE TABLE t(a UNIQUE, b);",
        "a",
        "UNIQUE")]
    [TestCase(
        "CREATE TABLE t(a UNIQUE);",
        "a",
        "UNIQUE")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE INDEX t_a ON t(a);",
        "a",
        "error in index t_a")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE INDEX t_a ON t((a << 1)) WHERE b > 0;",
        "a",
        "error in index t_a")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE INDEX t_b ON t((a << 1)) WHERE b > 0;",
        "b",
        "error in index t_b")]
    [TestCase(
        "CREATE TABLE t(a, b CHECK(a > 0), c);",
        "a",
        "error in table t")]
    [TestCase(
        "CREATE TABLE t(a, b GENERATED ALWAYS AS (a + 1) VIRTUAL, c);",
        "a",
        "error in table t")]
    [TestCase(
        "CREATE TABLE p(id PRIMARY KEY); "
            + "CREATE TABLE t(a, b, FOREIGN KEY(a) REFERENCES p(id));",
        "a",
        "error in table t")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE VIEW dependent_view AS SELECT a FROM t;",
        "a",
        "error in view dependent_view")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE VIEW base_view AS SELECT * FROM t; "
            + "CREATE VIEW dependent_view AS SELECT a FROM base_view;",
        "a",
        "error in view dependent_view")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE TABLE audit(value); "
            + "CREATE TRIGGER dependent_trigger AFTER INSERT ON t "
            + "BEGIN UPDATE t SET b = a; END;",
        "a",
        "error in trigger dependent_trigger")]
    public void DropColumnRejectsTheSameRetainedDependenciesAsSqlite(
        string setup,
        string column,
        string managedMessage)
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        Execute(managed, setup);
        Execute(sqlite, setup);

        var managedDrop = () => Execute(managed, $"ALTER TABLE t DROP COLUMN {column};");
        var sqliteDrop = () => Execute(sqlite, $"ALTER TABLE t DROP COLUMN {column};");

        managedDrop.Should().Throw<Exception>().WithMessage($"*{managedMessage}*");
        sqliteDrop.Should().Throw<Exception>();
        ReadRows(managed, "PRAGMA table_info(t);").Should().HaveCount(
            ReadRows(sqlite, "PRAGMA table_info(t);").Count);
    }

    [Test]
    public void DropColumnMatchesSqliteForInlineForeignKeysAndWithoutRowidTables()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        const string setup = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            INSERT INTO parent VALUES (1);
            CREATE TABLE child(
                id INTEGER PRIMARY KEY,
                parent_id INTEGER REFERENCES parent(id),
                keep TEXT DEFAULT 'kept'
            );
            INSERT INTO child VALUES (17, 1, 'value');
            CREATE TABLE keyed(
                tenant TEXT COLLATE NOCASE,
                sequence INTEGER,
                removed TEXT,
                value TEXT,
                PRIMARY KEY(tenant, sequence DESC)
            ) WITHOUT ROWID;
            CREATE INDEX keyed_value ON keyed(value DESC);
            INSERT INTO keyed VALUES ('alpha', 2, 'gone', 'preserved');
            """;

        Execute(managed, setup);
        Execute(sqlite, setup);
        Execute(managed, "ALTER TABLE child DROP COLUMN parent_id; ALTER TABLE keyed DROP removed;");
        Execute(sqlite, "ALTER TABLE child DROP COLUMN parent_id; ALTER TABLE keyed DROP removed;");

        ReadRows(managed, "SELECT rowid, * FROM child;")
            .Should().Equal(ReadRows(sqlite, "SELECT rowid, * FROM child;"));
        ReadRows(managed, "SELECT * FROM keyed;")
            .Should().Equal(ReadRows(sqlite, "SELECT * FROM keyed;"));
        ReadRows(managed, "PRAGMA index_info(keyed_value);")
            .Should().Equal(ReadRows(sqlite, "PRAGMA index_info(keyed_value);"));
        // SQLite's DROP COLUMN edit keeps the original table option text verbatim.
        Scalar<string>(managed, "SELECT sql FROM sqlite_schema WHERE name='keyed';")
            .Should().Be(Scalar<string>(sqlite, "SELECT sql FROM sqlite_schema WHERE name='keyed';"))
            .And.Contain("WITHOUT ROWID")
            .And.Contain("PRIMARY KEY(tenant, sequence DESC)")
            .And.NotContain("removed");
    }

    [Test]
    public void DropColumnCanExposeTheHiddenRowidWithoutChangingRowIdentity()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        const string setup = """
            CREATE TABLE data(rowid TEXT, removed TEXT, keep TEXT);
            INSERT INTO data(rowid, removed, keep) VALUES ('shadow', 'gone', 'preserved');
            CREATE VIEW rowid_view AS SELECT rowid, keep FROM data;
            """;

        Execute(managed, setup);
        Execute(sqlite, setup);
        Execute(managed, "ALTER TABLE data DROP COLUMN rowid;");
        Execute(sqlite, "ALTER TABLE data DROP COLUMN rowid;");

        ReadRows(managed, "SELECT rowid, keep FROM data;")
            .Should().Equal(ReadRows(sqlite, "SELECT rowid, keep FROM data;"));
        ReadRows(managed, "SELECT * FROM rowid_view;")
            .Should().Equal(ReadRows(sqlite, "SELECT * FROM rowid_view;"));
        ReadRows(managed, "SELECT rowid, keep FROM data;").Single()
            .Should().Be("1\u001fpreserved");
    }

    [Test]
    public void DropColumnCommitsAndRollsBackAtomicallyAndHonorsCancellation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, removed TEXT, keep TEXT);");
        Execute(connection, "INSERT INTO t VALUES (9, 'gone', 'preserved');");

        Execute(connection, "BEGIN; ALTER TABLE t DROP COLUMN removed; ROLLBACK;");
        ReadRows(connection, "PRAGMA table_info(t);").Select(row => row[1].AsText())
            .Should().Equal("id", "removed", "keep");

        using (var statement = connection.Prepare("ALTER TABLE t DROP COLUMN removed;"))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        }
        ReadRows(connection, "SELECT rowid, * FROM t;").Single()
            .Should().Equal(
                SqlValue.Integer(9),
                SqlValue.Integer(9),
                SqlValue.Text("gone"),
                SqlValue.Text("preserved"));

        Execute(connection, "BEGIN; ALTER TABLE t DROP COLUMN removed; COMMIT;");
        ReadRows(connection, "SELECT rowid, * FROM t;").Single()
            .Should().Equal(SqlValue.Integer(9), SqlValue.Integer(9), SqlValue.Text("preserved"));
    }

    [Test]
    public void PersistedDropReopensAndPassesMicrosoftDataSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var managed = OpenManagedFile(path))
            {
                Execute(
                    managed,
                    """
                    CREATE TABLE data(id INTEGER PRIMARY KEY, retained TEXT, removed BLOB, tail INTEGER);
                    CREATE INDEX data_tail ON data(tail DESC);
                    INSERT INTO data VALUES (71, 'preserved', X'010203', 5);
                    ALTER TABLE data DROP COLUMN removed;
                    """);
            }

            ManagedSqliteConnection.ClearAllPools();
            MsData.SqliteConnection.ClearAllPools();
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Scalar<string>(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
                ReadRows(sqlite, "SELECT rowid, id, retained, tail FROM data;").Single()
                    .Should().Be("71\u001f71\u001fpreserved\u001f5");
            }

            using var reopened = OpenManagedFile(path);
            ReadRows(reopened, "SELECT rowid, id, retained, tail FROM data;").Single()
                .Should().Be("71\u001f71\u001fpreserved\u001f5");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PersistFailureLeavesTheOriginalCatalogAndRowsRecoverable()
    {
        var faults = new DeterministicFaultInjector();
        var fileSystem = new InMemoryFileSystem(faults);
        const string path = "drop-column-failure.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                """
                CREATE TABLE data(id INTEGER PRIMARY KEY, removed TEXT, keep TEXT);
                CREATE INDEX data_keep ON data(keep DESC);
                INSERT INTO data VALUES (1, 'gone', 'preserved');
                """);
            faults.FailOnOccurrence(
                FileSystemOperation.Write,
                faults.GetOperationCount(FileSystemOperation.Write) + 3);
            Assert.Throws<IOException>(
                () => Execute(connection, "ALTER TABLE data DROP COLUMN removed;"));
        }

        using var recovered = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var recoveredConnection = recovered.Connect();
        ReadRows(recoveredConnection, "PRAGMA table_info(data);").Select(row => row[1].AsText())
            .Should().Equal("id", "removed", "keep");
        ReadRows(recoveredConnection, "SELECT rowid, * FROM data;").Single()
            .Should().Equal(
                SqlValue.Integer(1),
                SqlValue.Integer(1),
                SqlValue.Text("gone"),
                SqlValue.Text("preserved"));
    }

    [TestCase("WAL", false)]
    [TestCase("DELETE", false)]
    [TestCase("WAL", true)]
    [TestCase("DELETE", true)]
    public void DropColumnSurvivesJournalModesEncryptionPageMigrationAndSchemaOverflow(
        string journalMode,
        bool encrypted)
    {
        var inner = new InMemoryFileSystem();
        using var encryption = encrypted
            ? AhtolaEncryptionOptions.FromHex(AhtolaEncryptionCipher.Aes256Gcm, Aes256Key)
            : null;
        IFileSystem fileSystem = encryption is null
            ? inner
            : new AhtolaEncryptionFileSystem(inner, encryption);
        var path = $"drop-{journalMode.ToLowerInvariant()}-{encrypted}.db";
        var longDefault = new string('x', 6_000);

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                $"CREATE TABLE data(id INTEGER PRIMARY KEY, retained TEXT DEFAULT '{longDefault}', "
                    + "removed TEXT, tail INTEGER);");
            Execute(connection, "CREATE INDEX data_tail ON data(tail DESC);");
            Execute(connection, "INSERT INTO data(id, removed, tail) VALUES (5, 'gone', 9);");
            Execute(connection, $"PRAGMA journal_mode={journalMode};");
            if (journalMode == "DELETE")
                Execute(connection, "PRAGMA page_size=1024; VACUUM;");
            Execute(connection, "ALTER TABLE data DROP COLUMN removed;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT rowid, id, length(retained), tail FROM data;").Single()
            .Should().Equal(
                SqlValue.Integer(5),
                SqlValue.Integer(5),
                SqlValue.Integer(longDefault.Length),
                SqlValue.Integer(9));
        ReadRows(reopenedConnection, "PRAGMA table_info(data);").Select(row => row[1].AsText())
            .Should().Equal("id", "retained", "tail");
        if (journalMode == "DELETE")
            ReadValue(reopenedConnection, "PRAGMA page_size;").Should().Be(SqlValue.Integer(1024));
    }

    [Test]
    public void DropColumnRoutesThroughAttachPoolingAndBackup()
    {
        var mainPath = CreateDatabasePath();
        var attachedPath = CreateDatabasePath();
        var backupPath = CreateDatabasePath();
        try
        {
            using var connection = OpenManagedFile(mainPath);
            Execute(
                connection,
                """
                CREATE TABLE main_data(id INTEGER PRIMARY KEY, removed TEXT, keep TEXT);
                INSERT INTO main_data VALUES (11, 'gone', 'main');
                ALTER TABLE main_data DROP COLUMN removed;
                """);
            Execute(
                connection,
                $"ATTACH DATABASE '{EscapeSqlLiteral(attachedPath)}' AS aux;"
                    + "CREATE TABLE aux.data(id INTEGER PRIMARY KEY, removed TEXT, keep TEXT);"
                    + "INSERT INTO aux.data VALUES (12, 'gone', 'attached');"
                    + "ALTER TABLE aux.data DROP COLUMN removed;"
                    + "DETACH DATABASE aux;");

            var physical = connection.ManagedConnection;
            connection.Close();
            connection.Open();
            connection.ManagedConnection.Should().BeSameAs(physical);
            Execute(connection, $"ATTACH DATABASE '{EscapeSqlLiteral(attachedPath)}' AS aux;");
            ReadRows(connection, "SELECT rowid, * FROM aux.data;").Single()
                .Should().Be("12\u001f12\u001fattached");
            Execute(connection, "DETACH DATABASE aux;");

            using var backup = OpenManagedFile(backupPath);
            connection.BackupDatabase(backup);
            ReadRows(backup, "SELECT rowid, * FROM main_data;").Single()
                .Should().Be("11\u001f11\u001fmain");
        }
        finally
        {
            ManagedSqliteConnection.ClearAllPools();
            DeleteDatabase(mainPath);
            DeleteDatabase(attachedPath);
            DeleteDatabase(backupPath);
        }
    }

    private static ManagedSqliteConnection OpenManagedMemory()
    {
        var connection = new ManagedSqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static MsData.SqliteConnection OpenMicrosoftMemory()
    {
        var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static ManagedSqliteConnection OpenManagedFile(string path)
    {
        var connection = new ManagedSqliteConnection(
            $"Data Source={path};Local Provider=Managed;Pooling=True");
        connection.Open();
        return connection;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadRows(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "\u001f",
                Enumerable.Range(0, reader.FieldCount).Select(index => FormatValue(reader.GetValue(index)))));
        }
        return rows;
    }

    private static T Scalar<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string FormatValue(object value)
        => value switch
        {
            DBNull => "<null>",
            byte[] bytes => Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };

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

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
            rows.Add(Enumerable.Range(0, statement.ColumnCount).Select(statement.GetValue).ToArray());
        return rows;
    }

    private static SqlValue ReadValue(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        var value = statement.GetValue(0);
        statement.Step().Should().Be(StatementStepResult.Done);
        return value;
    }

    private static string CreateDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"Ahtola-drop-column-{Guid.NewGuid():N}.db");

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static void DeleteDatabase(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }
}
