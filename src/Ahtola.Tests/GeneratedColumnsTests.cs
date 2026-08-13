using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

// Coverage for Turso-compatible VIRTUAL generated columns. Runtime behavior is cross-checked
// against SQLite: computed values and affinity, dependency ordering, recompute on UPDATE, the
// generated-column exclusion from the default INSERT column list and from PRAGMA table_info,
// and the family of CREATE/DML rejections. The bounded deterministic-function allow-list remains
// a deliberate managed-engine boundary.
public class GeneratedColumnsTests
{
    [Test]
    public void VirtualGeneratedColumnComputesValue()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, v AS (a + 1))",
                "INSERT INTO t(a) VALUES (1)",
                "INSERT INTO t(a) VALUES (10)",
            ],
            "SELECT a, v FROM t ORDER BY a");
    }

    [Test]
    [TestCase("CREATE TABLE t(a INT, v AS (a * 2) STORED)")]
    [TestCase("CREATE TABLE t(a INT, v INT GENERATED ALWAYS AS (a * 2) STORED)")]
    public void StoredGeneratedColumnIsRejectedLikeTurso(string sql)
    {
        CaptureManagedError([], sql).Should().Be("Stored generated columns are not supported");
    }

    [Test]
    public void GeneratedAlwaysSyntaxComputesValue()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, b INT, v INT GENERATED ALWAYS AS (a + b) VIRTUAL)",
                "INSERT INTO t(a, b) VALUES (2, 5)",
            ],
            "SELECT a, b, v FROM t");
    }

    [Test]
    public void GeneratedColumnAppliesDeclaredTypeAffinity()
    {
        // The declared TEXT affinity coerces the numeric result of the expression to text,
        // exactly as SQLite coerces a generated value to its declared type.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, v TEXT AS (a + 1))",
                "INSERT INTO t(a) VALUES (41)",
            ],
            "SELECT a, v, typeof(v) AS vt FROM t");
    }

    [Test]
    public void GeneratedColumnConcatenatesText()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT, v AS (a || '!') VIRTUAL)",
                "INSERT INTO t(a) VALUES ('hi')",
            ],
            "SELECT a, v FROM t");
    }

    [Test]
    public void GeneratedColumnReferencesAnotherGeneratedColumn()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, b AS (a + 1), c AS (b + 1))",
                "INSERT INTO t(a) VALUES (10)",
            ],
            "SELECT a, b, c FROM t");
    }

    [Test]
    public void GeneratedColumnResolvesForwardReference()
    {
        // c is declared before the generated column b it depends on; SQLite resolves the
        // dependency regardless of declaration order, so the managed topological ordering
        // must produce the same result.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, c AS (b + 1), b AS (a + 1))",
                "INSERT INTO t(a) VALUES (10)",
            ],
            "SELECT a, b, c FROM t");
    }

    [Test]
    public void GeneratedColumnUsesAllowedDeterministicFunctions()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a TEXT, u AS (upper(a)) VIRTUAL, n AS (length(a)))",
                "INSERT INTO t(a) VALUES ('abc')",
            ],
            "SELECT a, u, n FROM t");
    }

    [Test]
    public void DefaultInsertColumnListExcludesGeneratedColumn()
    {
        // With v generated, a bare INSERT ... VALUES supplies only the non-generated column.
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, v AS (a + 1))",
                "INSERT INTO t VALUES (5)",
            ],
            "SELECT a, v FROM t");
    }

    [Test]
    public void UpdateRecomputesGeneratedColumn()
    {
        AssertMatchesSqlite(
            [
                "CREATE TABLE t(a INT, v AS (a + 1) VIRTUAL)",
                "INSERT INTO t(a) VALUES (1)",
                "UPDATE t SET a = 100",
            ],
            "SELECT a, v FROM t");
    }

    [Test]
    public void PragmaTableInfoExcludesGeneratedColumn()
    {
        AssertMatchesSqlite(
            ["CREATE TABLE t(a INT, b TEXT, v AS (a + 1))"],
            "PRAGMA table_info(t)");
    }

    [Test]
    public void InsertingIntoGeneratedColumnIsRejected()
    {
        AssertErrorMatchesSqlite(
            ["CREATE TABLE t(a INT, v AS (a + 1))"],
            "INSERT INTO t(a, v) VALUES (1, 2)");
    }

    [Test]
    public void UpdatingGeneratedColumnIsRejected()
    {
        AssertErrorMatchesSqlite(
            [
                "CREATE TABLE t(a INT, v AS (a + 1))",
                "INSERT INTO t(a) VALUES (1)",
            ],
            "UPDATE t SET v = 5");
    }

    [Test]
    public void GeneratedNotNullConstraintFailsWithTableQualifiedMessage()
    {
        AssertErrorMatchesSqlite(
            ["CREATE TABLE t(a INT, v AS (a) NOT NULL)"],
            "INSERT INTO t(a) VALUES (NULL)");
    }

    [Test]
    public void GenerationExpressionReferencingUnknownColumnIsRejected()
    {
        AssertErrorMatchesSqlite(
            [],
            "CREATE TABLE t(a INT, v AS (zzz + 1))");
    }

    [Test]
    public void GenerationExpressionReferencingRowidIsRejected()
    {
        AssertErrorMatchesSqlite(
            [],
            "CREATE TABLE t(a INT, v AS (rowid + 1))");
    }

    [Test]
    public void DefaultOnGeneratedColumnIsRejected()
    {
        AssertBothReject(
            [],
            "CREATE TABLE t(a INT, v AS (a) DEFAULT 5)");
    }

    [Test]
    public void GeneratedColumnInPrimaryKeyIsRejected()
    {
        AssertBothReject(
            [],
            "CREATE TABLE t(a INT, v AS (a + 1), PRIMARY KEY(v))");
    }

    [Test]
    public void TableOfOnlyGeneratedColumnsIsRejected()
    {
        AssertBothReject(
            [],
            "CREATE TABLE t(v AS (1))");
    }

    [Test]
    public void GeneratedColumnDependencyLoopIsRejected()
    {
        // Both engines reject the cycle; the column named in the message can differ by
        // traversal order, so the managed message is only asserted to name the loop.
        var managedMessage = CaptureManagedError([], "CREATE TABLE t(a INT, v AS (w), w AS (v))");
        managedMessage.Should().Contain("generated column loop on");

        AssertBothReject([], "CREATE TABLE t(a INT, v AS (w), w AS (v))");
    }

    [Test]
    public void NonDeterministicFunctionInGenerationIsRejectedByManagedEngine()
    {
        // random() is non-deterministic. SQLite rejects it too, and the managed engine now
        // uses SQLite's exact diagnostic.
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        var act = () => Execute(connection, "CREATE TABLE t(a INT, v AS (random()))");
        act.Should().Throw<EmbeddedSqlException>().WithMessage("non-deterministic functions prohibited in generated columns");
    }

    [Test]
    public void BoundParameterInGenerationIsRejectedByManagedEngine()
    {
        var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // Bind the parameter so execution reaches generated-column validation rather than
        // stopping at the unbound-parameter guard; the expression is still rejected because
        // a generated column must be a deterministic function of the row's own columns.
        var act = () =>
        {
            using var statement = connection.Prepare("CREATE TABLE t(a INT, v AS (a + ?))");
            statement.Bind(1, SqlValue.Integer(1));
            statement.Step();
        };
        act.Should().Throw<EmbeddedSqlException>().WithMessage("bind parameters prohibited in generated columns");
    }

    [Test]
    public void AlterTableAddStoredGeneratedColumnIsRejectedLikeTurso()
    {
        CaptureManagedError(
            ["CREATE TABLE t(a INT)"],
            "ALTER TABLE t ADD COLUMN v AS (a * 2) STORED")
            .Should().Be("cannot add a STORED column");
    }

    [Test]
    public void VirtualGeneratedColumnPersistsAndIsReadableByRealSqlite()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE t(a INT, v AS (a + 1) VIRTUAL, label TEXT AS (a || '!') VIRTUAL);");
                Execute(connection, "INSERT INTO t(a) VALUES (10);");
                Execute(connection, "INSERT INTO t(a) VALUES (20);");
            }

            var verifyPath = path + ".verify.db";
            File.Copy(path, verifyPath, overwrite: true);
            try
            {
                using var real = new MsData.SqliteConnection($"Data Source={verifyPath}");
                real.Open();

                // integrity_check evaluates the persisted VIRTUAL expressions, proving the
                // managed schema and SQLite computation remain compatible.
                using var integrity = real.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check;";
                integrity.ExecuteScalar().Should().Be("ok");

                using var query = real.CreateCommand();
                query.CommandText = "SELECT a, v, label FROM t ORDER BY a;";
                using var reader = query.ExecuteReader();

                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(10);
                reader.GetInt64(1).Should().Be(11);
                reader.GetString(2).Should().Be("10!");

                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(20);
                reader.GetInt64(1).Should().Be(21);
                reader.GetString(2).Should().Be("20!");
            }
            finally
            {
                MsData.SqliteConnection.ClearAllPools();
                DeletePhysicalDatabase(verifyPath);
            }
        }
        finally
        {
            DeletePhysicalDatabase(path);
        }
    }

    [Test]
    public void VirtualGeneratedColumnRoundTripsAcrossManagedReopen()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("virtual-generated.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE t(a INT, v AS (a + 1) VIRTUAL);");
            Execute(connection, "INSERT INTO t(a) VALUES (5);");
        }

        using (var reopened = EmbeddedDatabase.OpenFile("virtual-generated.db", fileSystem))
        using (var connection = reopened.Connect())
        {
            var rows = Query(connection, "SELECT a, v FROM t;");
            rows.Should().ContainSingle();
            rows[0][0].AsInteger().Should().Be(5);
            rows[0][1].AsInteger().Should().Be(6);

            // The reopened, still-generated column continues to recompute on write.
            Execute(connection, "UPDATE t SET a = 40;");
            var updated = Query(connection, "SELECT a, v FROM t;");
            updated[0][1].AsInteger().Should().Be(41);
        }
    }

    [Test]
    public void VirtualGeneratedColumnsAndDependenciesRoundTripWithoutStoredRecordFields()
    {
        var path = CreatePhysicalDatabasePath();
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(
                    connection,
                    """
                    CREATE TABLE t(
                        a INT,
                        c INT CONSTRAINT generated_c GENERATED ALWAYS AS (b + 1) VIRTUAL
                            CONSTRAINT positive_c CHECK (c > 0),
                        b INT CONSTRAINT generated_b AS (a + 1) VIRTUAL,
                        d INT AS (c + 1) VIRTUAL,
                        e INT AS (d + 1) VIRTUAL,
                        CONSTRAINT unique_c UNIQUE(c) ON CONFLICT IGNORE
                    );
                    """);
                Execute(connection, "INSERT INTO t(a) VALUES (10), (10), (20);");
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path))
            using (var connection = reopened.Connect())
            {
                var rows = Query(connection, "SELECT a, b, c, d, e FROM t ORDER BY a;");
                rows.Should().HaveCount(2);
                rows[0].Should().Equal(
                    SqlValue.Integer(10),
                    SqlValue.Integer(11),
                    SqlValue.Integer(12),
                    SqlValue.Integer(13),
                    SqlValue.Integer(14));
                rows[1].Should().Equal(
                    SqlValue.Integer(20),
                    SqlValue.Integer(21),
                    SqlValue.Integer(22),
                    SqlValue.Integer(23),
                    SqlValue.Integer(24));
                Execute(connection, "UPDATE t SET a = 30 WHERE a = 20;");
                Query(connection, "SELECT b, c, d, e FROM t WHERE a = 30;").Single()
                    .Should().Equal(
                        SqlValue.Integer(31),
                        SqlValue.Integer(32),
                        SqlValue.Integer(33),
                        SqlValue.Integer(34));
            }

            using var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False");
            sqlite.Open();
            Scalar(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
            Scalar(sqlite, "SELECT COUNT(*) FROM t;").Should().Be(2L);
            Scalar(sqlite, "SELECT c FROM t WHERE a = 30;").Should().Be(32L);
            Scalar(sqlite, "SELECT e FROM t WHERE a = 30;").Should().Be(34L);
            Scalar(sqlite, "SELECT hidden FROM pragma_table_xinfo('t') WHERE name = 'b';").Should().Be(2L);
            Scalar(sqlite, "SELECT sql FROM sqlite_schema WHERE name = 't';")
                .Should().BeOfType<string>()
                .Which.Should().Contain("CONSTRAINT generated_c GENERATED ALWAYS AS (b + 1) VIRTUAL")
                .And.Contain("CONSTRAINT positive_c CHECK (c > 0)")
                .And.Contain("UNIQUE(c) ON CONFLICT IGNORE");
        }
        finally
        {
            MsData.SqliteConnection.ClearAllPools();
            DeletePhysicalDatabase(path);
        }
    }

    private static void AssertMatchesSqlite(IReadOnlyList<string> setup, string query)
    {
        var managed = RunManaged(setup, query);
        var reference = RunSqlite(setup, query);

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

        // Microsoft.Data.Sqlite wraps the core message as "SQLite Error NN: '<message>'.",
        // so the managed engine's message must appear verbatim inside SQLite's.
        sqliteMessage.Should().Contain(
            managedMessage,
            "the managed error should match the SQLite error text");
    }

    private static void AssertBothReject(IReadOnlyList<string> setup, string query)
    {
        // Some rejections are semantically identical between engines but carry engine-
        // specific wording (or name a column chosen by traversal order); here we only pin
        // that both engines reject the statement.
        var managed = () => CaptureManagedError(setup, query);
        managed.Should().NotThrow("the managed engine should reject with an EmbeddedSqlException");

        using var connection = new MsData.SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var statement in setup)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        var sqlite = () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            command.ExecuteNonQuery();
        };
        sqlite.Should().Throw<MsData.SqliteException>("SQLite should reject the same statement");
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

    private static List<SqlValue[]> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var row = new SqlValue[statement.GetColumnCount()];
            for (var ordinal = 0; ordinal < row.Length; ordinal++)
                row[ordinal] = statement.GetValue(ordinal);

            rows.Add(row);
        }

        return rows;
    }

    private static object? Scalar(MsData.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string CreatePhysicalDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "generated-column-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"generated-{Guid.NewGuid():N}.db");
    }

    private static void DeletePhysicalDatabase(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
                File.Delete(candidate);
        }
    }

    private sealed record QueryOutput(string[] Columns, IReadOnlyList<SqlValue[]> Rows);

    private sealed record ReferenceOutput(string[] Columns, IReadOnlyList<object?[]> Rows);
}
