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
        ]).Should().Be(7);
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(9),
            SqlValue.Text("Rose"),
            SqlValue.Text("Red flower"),
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
        matches[0].Should().Equal(
            SqlValue.Integer(7), SqlValue.Text("Orchid"), SqlValue.Text("Purple flower"), SqlValue.Null);
        matches[1].Should().Equal(
            SqlValue.Integer(9), SqlValue.Text("Rose"), SqlValue.Text("Red flower"), SqlValue.Null);

        table.Update(
        [
            SqlValue.Integer(7),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
        ]).Should().BeNull();
        matches = ReadRows(table, plan, [SqlValue.Text("orchid OR rose")]);
        matches.Should().ContainSingle();
        matches[0].Should().Equal(
            SqlValue.Integer(9), SqlValue.Text("Rose"), SqlValue.Text("Red flower"), SqlValue.Null);

        table.Begin();
        table.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(11),
            SqlValue.Text("Lily"),
            SqlValue.Text("White flower"),
            SqlValue.Null,
        ]);
        table.Rollback();
        ReadRows(table, plan, [SqlValue.Text("lily")]).Should().BeEmpty();
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
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Real(1.5),
            SqlValue.Integer(2),
        ]);
        insertFractionalCoordinate.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*rtree_i32 coordinates must be integers*");

        Action insertOutOfRangeCoordinate = () => integerTable.Update(
        [
            SqlValue.Null,
            SqlValue.Integer(1),
            SqlValue.Integer(1),
            SqlValue.Integer((long)int.MaxValue + 1),
            SqlValue.Integer(2),
        ]);
        insertOutOfRangeCoordinate.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*signed 32-bit integer*");
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
            "INSERT INTO bounds VALUES (1, 0, 10), (2, 'not-a-coordinate', 20);");

        insert.Should().Throw<EmbeddedSqlException>().WithMessage("*rtree coordinates must be numeric*");
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
            new ManagedVirtualTablePersistencePayload(2, []));
        unsupportedRtreeVersion.Should().Throw<EmbeddedSqlException>()
            .WithMessage("*unsupported rtree*version 2");

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
