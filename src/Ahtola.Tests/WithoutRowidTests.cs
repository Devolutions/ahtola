using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Coverage for the managed engine's SQLite-compatible WITHOUT ROWID subset. Every
// behaviour is cross-checked against a real SQLite build (Microsoft.Data.Sqlite): rows are
// stored and scanned in primary-key order (honouring composite tuples and per-column
// ASC/DESC), the primary key is implicitly NOT NULL and unique, INTEGER PRIMARY KEY is an
// ordinary stored column (no rowid alias), and rowid references are rejected.
// The file engine persists bounded WITHOUT ROWID tables with one ascending BINARY primary-key
// column as an index leaf. Unsupported key shapes and multi-page trees remain rejected.
public class WithoutRowidTests
{
    [Test]
    public void RowsAreScannedInPrimaryKeyOrder()
    {
        // Inserted out of key order; a full scan must still observe primary-key order.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (3, 'c')",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
                "INSERT INTO t(k, v) VALUES (2, 'b')",
            ],
            "SELECT k, v FROM t");
    }

    [Test]
    public void TextPrimaryKeyIsScannedInOrder()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(k TEXT PRIMARY KEY, v INT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES ('charlie', 3)",
                "INSERT INTO t(k, v) VALUES ('alpha', 1)",
                "INSERT INTO t(k, v) VALUES ('bravo', 2)",
            ],
            "SELECT k, v FROM t");
    }

    [Test]
    public void DescendingPrimaryKeyIsScannedInReverseOrder()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER, v TEXT, PRIMARY KEY(k DESC)) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
                "INSERT INTO t(k, v) VALUES (3, 'c')",
                "INSERT INTO t(k, v) VALUES (2, 'b')",
            ],
            "SELECT k, v FROM t");
    }

    [Test]
    public void CompositePrimaryKeyIsScannedInTupleOrder()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, b INT, v TEXT, PRIMARY KEY(a, b)) WITHOUT ROWID",
                "INSERT INTO t(a, b, v) VALUES (2, 1, 'x')",
                "INSERT INTO t(a, b, v) VALUES (1, 2, 'y')",
                "INSERT INTO t(a, b, v) VALUES (1, 1, 'z')",
            ],
            "SELECT a, b, v FROM t");
    }

    [Test]
    public void CollatedCompositePrimaryKeyControlsNaturalScanOrder()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT, b INT, v TEXT, PRIMARY KEY(a COLLATE NOCASE, b DESC)) WITHOUT ROWID",
                "INSERT INTO t VALUES ('beta', 1, 'b1')",
                "INSERT INTO t VALUES ('Alpha', 1, 'a1')",
                "INSERT INTO t VALUES ('alpha', 3, 'a3')",
                "INSERT INTO t VALUES ('charlie', 2, 'c2')",
            ],
            "SELECT a, b, v FROM t");
    }

    [Test]
    public void UpdateToPrimaryKeyReordersScan()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
                "INSERT INTO t(k, v) VALUES (2, 'b')",
                "UPDATE t SET k = 5 WHERE v = 'a'",
            ],
            "SELECT k, v FROM t");
    }

    [Test]
    public void DeleteFromWithoutRowidTable()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
                "INSERT INTO t(k, v) VALUES (2, 'b')",
                "INSERT INTO t(k, v) VALUES (3, 'c')",
                "DELETE FROM t WHERE k = 2",
            ],
            "SELECT k, v FROM t");
    }

    [Test]
    public void PragmaTableInfoReportsPrimaryKeyOrdinalAndNotNull()
    {
        AssertMatchesSqlite(
            ["CREATE TABLE t(a INT, b INT, v TEXT, PRIMARY KEY(b, a)) WITHOUT ROWID"],
            "PRAGMA table_info(t)");
    }

    [Test]
    public void IntegerPrimaryKeyIsAStoredColumnNotARowidAlias()
    {
        // In a WITHOUT ROWID table INTEGER PRIMARY KEY does not alias a rowid; selecting the
        // rowid pseudo-column must fail exactly as it does in SQLite.
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
            ],
            "SELECT rowid FROM t");
    }

    [Test]
    public void RowidInWhereClauseIsRejected()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
            ],
            "SELECT v FROM t WHERE rowid = 1");
    }

    [Test]
    public void MissingPrimaryKeyIsRejected()
    {
        AssertErrorMatchesSqlite(
            [],
            "CREATE TABLE t(a INT, b TEXT) WITHOUT ROWID");
    }

    [Test]
    public void NullPrimaryKeyIsRejected()
    {
        AssertErrorMatchesSqlite(
            ["CREATE TABLE t(k INTEGER, v TEXT, PRIMARY KEY(k)) WITHOUT ROWID"],
            "INSERT INTO t(k, v) VALUES (NULL, 'a')");
    }

    [Test]
    public void DuplicatePrimaryKeyIsRejected()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a')",
            ],
            "INSERT INTO t(k, v) VALUES (1, 'b')");
    }

    [Test]
    public void UpdatingToDuplicatePrimaryKeyIsRejected()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID",
                "INSERT INTO t(k, v) VALUES (1, 'a'), (2, 'b')",
            ],
            "UPDATE t SET k = 1 WHERE k = 2");
    }

    [Test]
    public void InsertConflictAlgorithmsAndConstraintReplacementMatchSqlite()
    {
        AssertMatchesSqlite(
            [
                """
                CREATE TABLE t(
                    k TEXT COLLATE NOCASE PRIMARY KEY ON CONFLICT REPLACE,
                    alternate TEXT,
                    value TEXT,
                    UNIQUE(alternate) ON CONFLICT IGNORE
                ) WITHOUT ROWID
                """,
                "INSERT INTO t VALUES ('one', 'a', 'first')",
                "INSERT INTO t VALUES ('ONE', 'b', 'constraint-replaced')",
                "INSERT OR IGNORE INTO t VALUES ('two', 'b', 'ignored')",
                "INSERT OR REPLACE INTO t VALUES ('three', 'b', 'statement-replaced')",
                "INSERT INTO t VALUES ('four', NULL, 'null-one')",
                "INSERT INTO t VALUES ('five', NULL, 'null-two')",
                """
                INSERT INTO t VALUES ('THREE', 'c', 'upserted')
                ON CONFLICT(k) DO UPDATE SET value = excluded.value
                """,
            ],
            "SELECT k, alternate, value FROM t");
    }

    [Test]
    public void AbortFailAndRollbackConflictAtomicityMatchesSqlite()
    {
        var create = new[]
        {
            "CREATE TABLE t(k INTEGER PRIMARY KEY, value TEXT UNIQUE) WITHOUT ROWID",
            "INSERT INTO t VALUES (1, 'one')",
        };
        AssertStateAfterErrorMatchesSqlite(
            create,
            "INSERT OR ABORT INTO t VALUES (2, 'two'), (1, 'duplicate'), (3, 'three')",
            "SELECT k, value FROM t");
        AssertStateAfterErrorMatchesSqlite(
            create,
            "INSERT OR FAIL INTO t VALUES (2, 'two'), (1, 'duplicate'), (3, 'three')",
            "SELECT k, value FROM t");
        AssertStateAfterErrorMatchesSqlite(
            [.. create, "BEGIN", "INSERT INTO t VALUES (4, 'transaction-row')"],
            "INSERT OR ROLLBACK INTO t VALUES (2, 'two'), (1, 'duplicate')",
            "SELECT k, value FROM t");
    }

    [Test]
    public void WithoutRowidInsertDoesNotChangeLastInsertRowid()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE ordinary(value TEXT)",
                "INSERT INTO ordinary VALUES ('rowid-source')",
                "CREATE TABLE keyed(k INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID",
                "INSERT INTO keyed VALUES (99, 'without-rowid')",
                "INSERT INTO keyed VALUES (100, 'upsert') ON CONFLICT(k) DO UPDATE SET value = excluded.value",
            ],
            "SELECT last_insert_rowid() AS value");
    }

    [Test]
    public void UpdateReturningVisitsRowsInClusteredPrimaryKeyOrder()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY DESC, value TEXT) WITHOUT ROWID",
                "INSERT INTO t VALUES (1, 'a'), (3, 'c'), (2, 'b')",
            ],
            "UPDATE t SET k = k + 10 RETURNING k, value");
    }

    [Test]
    public void LimitedDmlUsesExplicitNullPlacementAndClusteredFallbackOrder()
    {
        var setup = new[]
        {
            """
            CREATE TABLE t(
                tenant TEXT,
                sequence INTEGER,
                rank INTEGER,
                value TEXT,
                PRIMARY KEY(tenant COLLATE NOCASE, sequence DESC)
            ) WITHOUT ROWID
            """,
            """
            INSERT INTO t VALUES
                ('Alpha', 2, NULL, 'a2'),
                ('alpha', 1, 2, 'a1'),
                ('beta', 3, 1, 'b3'),
                ('charlie', 4, NULL, 'c4')
            """,
        };

        OutputsShouldMatch(
            RunManaged(
                setup,
                """
                UPDATE t SET value = 'updated'
                RETURNING tenant, sequence
                ORDER BY rank ASC NULLS LAST, tenant COLLATE NOCASE ASC
                LIMIT 1
                """),
            RunSqlite(
                setup,
                """
                SELECT tenant, sequence FROM t
                ORDER BY rank ASC NULLS LAST, tenant COLLATE NOCASE ASC
                LIMIT 1
                """));
        OutputsShouldMatch(
            RunManaged(
                setup,
                """
                DELETE FROM t
                RETURNING tenant, sequence
                ORDER BY rank DESC NULLS FIRST, tenant COLLATE NOCASE DESC
                LIMIT 1
                """),
            RunSqlite(
                setup,
                """
                SELECT tenant, sequence FROM t
                ORDER BY rank DESC NULLS FIRST, tenant COLLATE NOCASE DESC
                LIMIT 1
                """));
        OutputsShouldMatch(
            RunManaged(setup, "UPDATE t SET value = 'clustered' RETURNING tenant, sequence LIMIT 1"),
            RunSqlite(setup, "SELECT tenant, sequence FROM t LIMIT 1"));
    }

    [Test]
    public void InsertAndDeleteReturningPreserveGeneratedValuesAndClusteredOrder()
    {
        AssertMatchesSqlite(
            [
                """
                CREATE TABLE t(
                    k INTEGER PRIMARY KEY DESC,
                    value INTEGER,
                    computed INTEGER GENERATED ALWAYS AS (value * 2) VIRTUAL
                ) WITHOUT ROWID
                """,
            ],
            "INSERT INTO t(k, value) VALUES (1, 10), (3, 30), (2, 20) RETURNING k, computed");
        AssertMatchesSqlite(
            [
                """
                CREATE TABLE t(
                    k INTEGER PRIMARY KEY DESC,
                    value INTEGER,
                    computed INTEGER GENERATED ALWAYS AS (value * 2) VIRTUAL
                ) WITHOUT ROWID
                """,
                "INSERT INTO t(k, value) VALUES (1, 10), (3, 30), (2, 20)",
            ],
            "DELETE FROM t WHERE value >= 20 RETURNING k, computed");
    }

    [TestCase("_rowid_")]
    [TestCase("oid")]
    public void EveryHiddenRowidSpellingIsRejected(string pseudoColumn)
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(k INTEGER PRIMARY KEY, value TEXT) WITHOUT ROWID",
                "INSERT INTO t VALUES (1, 'one')",
            ],
            $"SELECT {pseudoColumn} FROM t");
    }

    [Test]
    public void DuplicateCompositePrimaryKeyIsRejectedWithKeyColumnOrder()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(a INT, b INT, v TEXT, PRIMARY KEY(b, a)) WITHOUT ROWID",
                "INSERT INTO t(a, b, v) VALUES (1, 2, 'x')",
            ],
            "INSERT INTO t(a, b, v) VALUES (1, 2, 'y')");
    }

    [Test]
    public void BoundedWithoutRowidPersistenceSurvivesReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "bounded-without-rowid.db";

        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(k INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID;");
            Execute(connection, "INSERT INTO t VALUES (1, 'durable');");
            using var initialStatement = connection.Prepare("SELECT k, v FROM t;");
            initialStatement.Step().Should().Be(StatementStepResult.Row);
            initialStatement.GetValue(0).Should().Be(SqlValue.Integer(1));
            initialStatement.GetValue(1).Should().Be(SqlValue.Text("durable"));
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem);
        using var reopenedConnection = reopened.Connect();
        using var statement = reopenedConnection.Prepare("SELECT k, v FROM t;");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Integer(1));
        statement.GetValue(1).Should().Be(SqlValue.Text("durable"));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void TableLevelPrimaryKeyPersistsWithDeclarationOrder()
    {
        var fileSystem = new InMemoryFileSystem();
        using (var database = EmbeddedDatabase.OpenFile("table-pk.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INT, b INT, PRIMARY KEY(b, a));");
            Execute(connection, "INSERT INTO t VALUES (1, 2);");
        }

        using var reopened = EmbeddedDatabase.OpenFile("table-pk.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        using var statement = reopenedConnection.Prepare("PRAGMA table_info(t);");
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(5).AsInteger().Should().Be(2);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(5).AsInteger().Should().Be(1);
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var reference = RunSqlite(setup, query);

        OutputsShouldMatch(managed, reference);
    }

    private static void AssertStateAfterErrorMatchesSqlite(
        IReadOnlyList<string> setup,
        string failingStatement,
        string query)
    {
        var managed = RunManagedAfterError(setup, failingStatement, query);
        var reference = RunSqliteAfterError(setup, failingStatement, query);

        OutputsShouldMatch(managed, reference);
    }

    private static void OutputsShouldMatch(QueryOutput managed, ReferenceOutput reference)
    {
        managed.Columns.Should().Equal(reference.Columns, "column names should match SQLite");
        managed.Rows.Should().HaveCount(reference.Rows.Count);
        for (var row = 0; row < reference.Rows.Count; row++)
        {
            managed.Rows[row].Should().HaveCount(reference.Rows[row].Length, "row {0} width should match SQLite", row);
            for (var column = 0; column < reference.Rows[row].Length; column++)
                CellsShouldMatch(managed.Rows[row][column], reference.Rows[row][column], row, column);
        }
    }

    private static void AssertErrorMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managedMessage = CaptureManagedError(setup, query);
        var sqliteMessage = CaptureSqliteError(setup, query);

        sqliteMessage.Should().Contain(
            managedMessage,
            "the managed error should match the SQLite error text");
    }

    private static string CaptureManagedError(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        var exception = Assert.Throws<EmbeddedSqlException>(() =>
        {
            using var statement = connection.Prepare(query);
            while (statement.Step() == StatementStepResult.Row)
            {
            }
        });

        return exception!.Message;
    }

    private static string CaptureSqliteError(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<MsData.SqliteException>(() =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
            }
        });

        return exception!.Message;
    }

    private static QueryOutput RunManaged(IReadOnlyList<string> setup, string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);

        using var command = connection.Prepare(query);
        var columns = new string[command.GetColumnCount()];
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            columns[ordinal] = command.GetColumnName(ordinal);

        var rows = new List<SqlValue[]>();
        while (command.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[command.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = command.GetValue(ordinal);

            rows.Add(values);
        }

        return new QueryOutput(columns, rows);
    }

    private static QueryOutput RunManagedAfterError(
        IReadOnlyList<string> setup,
        string failingStatement,
        string query)
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        foreach (var statement in setup)
            Execute(connection, statement);
        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, failingStatement));

        return ReadManagedOutput(connection, query);
    }

    private static QueryOutput ReadManagedOutput(EmbeddedConnection connection, string query)
    {
        using var command = connection.Prepare(query);
        var columns = new string[command.GetColumnCount()];
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            columns[ordinal] = command.GetColumnName(ordinal);

        var rows = new List<SqlValue[]>();
        while (command.Step() == StatementStepResult.Row)
        {
            var values = new SqlValue[command.GetColumnCount()];
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
                values[ordinal] = command.GetValue(ordinal);
            rows.Add(values);
        }

        return new QueryOutput(columns, rows);
    }

    private static ReferenceOutput RunSqlite(IReadOnlyList<string> setup, string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = query;
        using var reader = queryCommand.ExecuteReader();
        var columns = new string[reader.FieldCount];
        for (var column = 0; column < columns.Length; column++)
            columns[column] = reader.GetName(column);

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);

            rows.Add(values);
        }

        return new ReferenceOutput(columns, rows);
    }

    private static ReferenceOutput RunSqliteAfterError(
        IReadOnlyList<string> setup,
        string failingStatement,
        string query)
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
        using (var failing = connection.CreateCommand())
        {
            failing.CommandText = failingStatement;
            Assert.Throws<MsData.SqliteException>(() => failing.ExecuteNonQuery());
        }

        using var queryCommand = connection.CreateCommand();
        queryCommand.CommandText = query;
        using var reader = queryCommand.ExecuteReader();
        var columns = new string[reader.FieldCount];
        for (var column = 0; column < columns.Length; column++)
            columns[column] = reader.GetName(column);

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var column = 0; column < values.Length; column++)
                values[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(values);
        }

        return new ReferenceOutput(columns, rows);
    }

    private static void CellsShouldMatch(SqlValue managed, object? reference, int row, int column)
    {
        var because = $"cell ({row},{column}) should match SQLite";
        switch (reference)
        {
            case null:
                managed.Kind.Should().Be(SqlValueKind.Null, because);
                break;
            case long integer:
                managed.Kind.Should().Be(SqlValueKind.Integer, because);
                managed.AsInteger().Should().Be(integer, because);
                break;
            case double real:
                managed.Kind.Should().Be(SqlValueKind.Real, because);
                managed.AsReal().Should().BeApproximately(real, 1e-9, because);
                break;
            case string text:
                managed.Kind.Should().Be(SqlValueKind.Text, because);
                managed.AsText().Should().Be(text, because);
                break;
            case byte[] blob:
                managed.Kind.Should().Be(SqlValueKind.Blob, because);
                managed.AsBlob().ToArray().Should().Equal(blob, because);
                break;
            default:
                managed.ToString().Should().Be(reference.ToString(), because);
                break;
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<SqlValue[]> Rows);

    private sealed record ReferenceOutput(string[] Columns, IReadOnlyList<object?[]> Rows);
}
