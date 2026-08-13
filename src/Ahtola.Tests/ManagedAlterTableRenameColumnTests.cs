using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;
using MsData = Microsoft.Data.Sqlite;
using Ahtola.Core;

namespace Ahtola.Tests;

/// <summary>
/// Differential coverage for <c>ALTER TABLE ... RENAME COLUMN</c>. SQLite reparses the stored SQL of
/// every dependent schema object and edits only the tokens that actually resolve to the renamed
/// column, so these tests pin that behaviour against Microsoft.Data.SQLite rather than asserting a
/// hand-written expectation.
/// </summary>
[NonParallelizable]
public sealed class ManagedAlterTableRenameColumnTests
{
    private const string RichSchema = """
        CREATE TABLE parent(pid INTEGER PRIMARY KEY, tag TEXT UNIQUE);
        INSERT INTO parent VALUES (1, 'alpha');
        CREATE TABLE t(
          id INTEGER PRIMARY KEY,
          old_col INTEGER CHECK(old_col > 0),
          note TEXT,
          doubled INTEGER GENERATED ALWAYS AS (old_col * 2) VIRTUAL,
          shifted INTEGER GENERATED ALWAYS AS (old_col + 100) VIRTUAL,
          tag TEXT REFERENCES parent(tag),
          CHECK(old_col <> 42 AND note <> 'old_col')
        );
        CREATE UNIQUE INDEX t_old_col ON t(old_col);
        CREATE INDEX expr_idx ON t(old_col + 1) WHERE old_col > 5;
        CREATE TABLE sibling(
          x INTEGER REFERENCES t(old_col),
          y INTEGER,
          FOREIGN KEY(y) REFERENCES t(old_col)
        );
        CREATE VIEW v AS SELECT old_col, note AS old_col_alias, 'old_col' AS lit FROM t WHERE old_col > 1;
        CREATE TABLE audit(msg TEXT);
        CREATE TRIGGER tr AFTER UPDATE OF old_col, note ON t WHEN new.old_col > old.old_col
        BEGIN
          INSERT INTO audit(msg) VALUES ('old_col: ' || old.old_col || '->' || new.old_col);
        END;
        INSERT INTO t(id, old_col, note, tag) VALUES (1, 7, 'seven', 'alpha');
        """;

    [Test]
    public void RenameColumnRewritesEveryDependentSchemaReferenceLikeSqlite()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        Execute(managed, RichSchema);
        Execute(sqlite, RichSchema);

        Execute(managed, "ALTER TABLE t RENAME COLUMN old_col TO new_col;");
        Execute(sqlite, "ALTER TABLE t RENAME COLUMN old_col TO new_col;");

        // Views and triggers keep their verbatim CREATE text in both engines, so the rewritten SQL
        // must match byte for byte.
        SchemaSql(managed, "v").Should().Be(SchemaSql(sqlite, "v"));
        SchemaSql(managed, "tr").Should().Be(SchemaSql(sqlite, "tr"));
        SchemaSql(managed, "tr").Should()
            .Contain("AFTER UPDATE OF new_col, note")
            .And.Contain("WHEN new.new_col > old.new_col")
            .And.Contain("old.new_col || '->' || new.new_col")
            .And.Contain("'old_col: '");

        // The managed engine performs the same surgical rename edits on the stored CREATE text
        // that SQLite applies (sqlite3_rename_token), so everything matches byte for byte.
        SchemaSql(managed, "t").Should().Be(SchemaSql(sqlite, "t"));
        SchemaSql(managed, "t_old_col").Should().Be(SchemaSql(sqlite, "t_old_col"));
        SchemaSql(managed, "expr_idx").Should().Be(SchemaSql(sqlite, "expr_idx"));
        SchemaSql(managed, "sibling").Should().Be(SchemaSql(sqlite, "sibling"));
        SchemaSql(managed, "t").Should()
            .Contain("CHECK(new_col > 0)")
            .And.Contain("(new_col * 2) VIRTUAL")
            .And.Contain("(new_col + 100)")
            .And.Contain("CHECK(new_col <> 42 AND note <> 'old_col')");
        SchemaSql(managed, "expr_idx").Should()
            .Contain("(new_col + 1)")
            .And.Contain("WHERE new_col > 5");
        SchemaSql(managed, "sibling").Should()
            .Contain("REFERENCES t(new_col)")
            .And.NotContain("old_col");

        ReadRows(managed, "PRAGMA table_xinfo(t);")
            .Should().Equal(ReadRows(sqlite, "PRAGMA table_xinfo(t);"));
        ReadRows(managed, "PRAGMA index_info(t_old_col);")
            .Should().Equal(ReadRows(sqlite, "PRAGMA index_info(t_old_col);"));
        ReadRows(managed, "PRAGMA foreign_key_list(sibling);")
            .Should().Equal(ReadRows(sqlite, "PRAGMA foreign_key_list(sibling);"));

        const string update = "UPDATE t SET new_col = 9 WHERE id = 1;";
        Execute(managed, update);
        Execute(sqlite, update);

        ReadRows(managed, "SELECT * FROM v;").Should().Equal(ReadRows(sqlite, "SELECT * FROM v;"));
        ReadRows(managed, "SELECT msg FROM audit;").Should().Equal(ReadRows(sqlite, "SELECT msg FROM audit;"));
        ReadRows(managed, "SELECT msg FROM audit;").Should().Equal("old_col: 7->9");
        ReadRows(managed, "SELECT id, new_col, doubled, shifted FROM t;")
            .Should().Equal(ReadRows(sqlite, "SELECT id, new_col, doubled, shifted FROM t;"));
        ReadRows(managed, "SELECT id, new_col, doubled, shifted FROM t;").Should().Equal("1\u001f9\u001f18\u001f109");
        ReadRows(managed, "SELECT count(*) FROM t INDEXED BY expr_idx WHERE new_col > 5;")
            .Should().Equal(ReadRows(sqlite, "SELECT count(*) FROM t INDEXED BY expr_idx WHERE new_col > 5;"));

        // The rewritten CHECK constraint still guards the renamed column.
        Throws(managed, "UPDATE t SET new_col = -1 WHERE id = 1;").Should().Contain("CHECK");
        Throws(sqlite, "UPDATE t SET new_col = -1 WHERE id = 1;").Should().Contain("CHECK");
    }

    [Test]
    public void RenameColumnLeavesLiteralsAliasesAndSubstringIdentifiersAlone()
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        const string setup = """
            CREATE TABLE t(a, b, ab, ba, CHECK (b <> 'b' AND ab <> 'b' AND ba IS NOT 'b'));
            CREATE TABLE other(b, keep);
            CREATE VIEW v AS
              SELECT t.b, ab, ba, 'b' AS b_literal, other.b AS other_b
              FROM t JOIN other ON other.keep = t.b
              WHERE t.b = 'b';
            CREATE VIEW aliased AS SELECT b AS b FROM t ORDER BY b;
            CREATE TABLE audit(msg);
            CREATE TRIGGER tr AFTER INSERT ON t
            BEGIN
              INSERT INTO other(b, keep) VALUES ('b', new.b);
              INSERT INTO audit(msg) SELECT 'b' || new.ab || new.ba;
            END;
            """;
        Execute(managed, setup);
        Execute(sqlite, setup);

        Execute(managed, "ALTER TABLE t RENAME COLUMN b TO renamed;");
        Execute(sqlite, "ALTER TABLE t RENAME COLUMN b TO renamed;");

        SchemaSql(managed, "v").Should().Be(SchemaSql(sqlite, "v"));
        SchemaSql(managed, "aliased").Should().Be(SchemaSql(sqlite, "aliased"));
        SchemaSql(managed, "tr").Should().Be(SchemaSql(sqlite, "tr"));
        SchemaSql(managed, "v").Should()
            .Contain("'b' AS b_literal")
            .And.Contain("other.b AS other_b")
            .And.Contain("SELECT t.renamed, ab, ba")
            .And.Contain("WHERE t.renamed = 'b'");
        SchemaSql(managed, "tr").Should()
            .Contain("INSERT INTO other(b, keep) VALUES ('b', new.renamed)")
            .And.Contain("SELECT 'b' || new.ab || new.ba");
        SchemaSql(managed, "t").Should()
            .Contain("CHECK (renamed <> 'b' AND ab <> 'b' AND ba IS NOT 'b')");
        SchemaSql(managed, "other").Should().Be(SchemaSql(sqlite, "other")).And.NotContain("renamed");
    }

    [Test]
    public void RenameColumnPropagatesImplicitViewColumnsAndTableFunctionArguments()
    {
        using var connection = OpenManagedMemory();
        Execute(
            connection,
            """
            CREATE TABLE t(a, b);
            INSERT INTO t VALUES(1, 'value');
            CREATE VIEW v1 AS SELECT a, b FROM t;
            CREATE VIEW v2 AS SELECT b FROM v1;
            CREATE VIEW v3 AS SELECT j.value FROM t JOIN json_each(json_array(t.b)) AS j;
            ALTER TABLE t RENAME COLUMN b TO c;
            """);

        SchemaSql(connection, "v1").Should().Be("CREATE VIEW v1 AS SELECT a, c FROM t");
        SchemaSql(connection, "v2").Should().Be("CREATE VIEW v2 AS SELECT c FROM v1");
        SchemaSql(connection, "v3").Should().Be(
            "CREATE VIEW v3 AS SELECT j.value FROM t JOIN json_each(json_array(t.c)) AS j");
        ReadRows(connection, "SELECT * FROM v2;").Should().Equal("value");
        ReadRows(connection, "SELECT * FROM v3;").Should().Equal("value");
    }

    [TestCase("z", "b", "CREATE VIEW v AS SELECT z FROM t WHERE z > 0")]
    [TestCase("\"z\"", "b", "CREATE VIEW v AS SELECT \"z\" FROM t WHERE \"z\" > 0")]
    [TestCase("\"z z\"", "b", "CREATE VIEW v AS SELECT \"z z\" FROM t WHERE \"z z\" > 0")]
    [TestCase("z", "\"b\"", "CREATE VIEW v AS SELECT \"z\" FROM t WHERE \"z\" > 0")]
    [TestCase("z", "[b]", "CREATE VIEW v AS SELECT \"z\" FROM t WHERE \"z\" > 0")]
    public void RenameColumnQuotesReplacementTokensLikeSqlite(
        string newName,
        string reference,
        string expectedView)
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        var setup = $"CREATE TABLE t(a, b); CREATE VIEW v AS SELECT {reference} FROM t WHERE {reference} > 0;";
        Execute(managed, setup);
        Execute(sqlite, setup);

        Execute(managed, $"ALTER TABLE t RENAME COLUMN b TO {newName};");
        Execute(sqlite, $"ALTER TABLE t RENAME COLUMN b TO {newName};");

        SchemaSql(managed, "v").Should().Be(SchemaSql(sqlite, "v")).And.Be(expectedView);
    }

    [TestCase(
        "CREATE TABLE t(a, b); CREATE TABLE audit(x); "
            + "CREATE TRIGGER tr AFTER INSERT ON t WHEN t.b > 0 BEGIN INSERT INTO audit VALUES(1); END;",
        "b",
        "no such column: t.b")]
    [TestCase(
        "CREATE TABLE t(a, b); CREATE TABLE u(b, z); CREATE VIEW v AS SELECT * FROM t JOIN u USING(b);",
        "b",
        "cannot join using column b")]
    [TestCase(
        "CREATE TABLE t(a, b); "
            + "CREATE TRIGGER tr AFTER INSERT ON t BEGIN INSERT INTO missing VALUES(new.b); END;",
        "b",
        "no such table: missing")]
    [TestCase("CREATE TABLE t(a, b);", "b", "duplicate column name: a")]
    public void RenameColumnRejectsTheSameSchemaHazardsAsSqlite(
        string setup,
        string column,
        string managedMessage)
    {
        using var managed = OpenManagedMemory();
        using var sqlite = OpenMicrosoftMemory();
        Execute(managed, setup);
        Execute(sqlite, setup);
        var target = managedMessage.StartsWith("duplicate", StringComparison.Ordinal) ? "a" : "renamed";

        Throws(managed, $"ALTER TABLE t RENAME COLUMN {column} TO {target};")
            .Should().Contain(managedMessage);
        Throws(sqlite, $"ALTER TABLE t RENAME COLUMN {column} TO {target};")
            .Should().NotBeEmpty();

        ReadRows(managed, "PRAGMA table_info(t);").Should().Equal(ReadRows(sqlite, "PRAGMA table_info(t);"));
        ReadRows(managed, "PRAGMA table_info(t);").Select(row => row.Split('\u001f')[1])
            .Should().Contain(column);
    }

    [Test]
    public void RenameColumnRollsBackAtomicallyAndHonorsCancellation()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            """
            CREATE TABLE t(id INTEGER PRIMARY KEY, old_col INTEGER CHECK(old_col > 0));
            CREATE VIEW v AS SELECT old_col FROM t;
            INSERT INTO t VALUES (1, 5);
            """);

        Execute(connection, "BEGIN; ALTER TABLE t RENAME COLUMN old_col TO new_col; ROLLBACK;");
        SchemaSql(connection, "t").Should().Contain("old_col");
        SchemaSql(connection, "v").Should().Contain("old_col");

        using (var statement = connection.Prepare("ALTER TABLE t RENAME COLUMN old_col TO new_col;"))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => statement.Step(cancellation.Token));
        }
        SchemaSql(connection, "v").Should().Contain("old_col");

        Execute(connection, "BEGIN; ALTER TABLE t RENAME COLUMN old_col TO new_col; COMMIT;");
        SchemaSql(connection, "t").Should().Contain("new_col").And.NotContain("old_col");
        SchemaSql(connection, "v").Should().Be("CREATE VIEW v AS SELECT new_col FROM t");
    }

    [Test]
    public void PersistedRenameReopensAndPassesMicrosoftDataSqliteIntegrityCheck()
    {
        var path = CreateDatabasePath();
        try
        {
            using (var managed = OpenManagedFile(path))
            {
                Execute(managed, RichSchema);
                Execute(managed, "ALTER TABLE t RENAME COLUMN old_col TO new_col;");
            }

            ManagedSqliteConnection.ClearAllPools();
            MsData.SqliteConnection.ClearAllPools();
            using (var sqlite = new MsData.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                sqlite.Open();
                Scalar<string>(sqlite, "PRAGMA integrity_check;").Should().Be("ok");
                Execute(sqlite, "UPDATE t SET new_col = 11 WHERE id = 1;");
                ReadRows(sqlite, "SELECT msg FROM audit;").Should().Equal("old_col: 7->11");
                ReadRows(sqlite, "SELECT * FROM v;").Should().Equal("11\u001fseven\u001fold_col");
                Throws(sqlite, "UPDATE t SET new_col = -1 WHERE id = 1;").Should().Contain("CHECK");
            }

            using var reopened = OpenManagedFile(path);
            ReadRows(reopened, "SELECT id, new_col, doubled FROM t;").Should().Equal("1\u001f11\u001f22");
        }
        finally
        {
            ManagedSqliteConnection.ClearAllPools();
            MsData.SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
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

    private static string Throws(DbConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
        Assert.Fail($"Expected '{sql}' to fail.");
        return string.Empty;
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
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => FormatValue(reader.GetValue(index)))));
        }
        return rows;
    }

    private static T Scalar<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string SchemaSql(DbConnection connection, string name)
        => Scalar<string>(connection, $"SELECT sql FROM sqlite_schema WHERE name='{name}';");

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

    private static string SchemaSql(EmbeddedConnection connection, string name)
    {
        using var statement = connection.Prepare(
            $"SELECT sql FROM sqlite_schema WHERE name='{name}';");
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsText();
    }

    private static string CreateDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"Ahtola-rename-column-{Guid.NewGuid():N}.db");

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
