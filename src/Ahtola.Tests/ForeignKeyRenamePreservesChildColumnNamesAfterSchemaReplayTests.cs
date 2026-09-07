using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>
/// Regression coverage for a defect fixed in <c>RenameColumnRewriter.Walker.WalkCreateTable</c>:
/// <c>ALTER TABLE parent RENAME COLUMN ... TO ...</c> was rewriting a dependent child table's
/// own column definitions (name, PRIMARY KEY/UNIQUE lists, FK child-column list) whenever they
/// happened to share the parent's pre-rename column spelling, even though only the parent's
/// column actually changed. The child's FOREIGN KEY parent-column reference is the only thing
/// that should ever follow the rename; the child's own schema must stay byte-for-byte itself.
/// </summary>
/// <remarks>
/// Mirrors turso-sqltests/alter_column.sqltest's alter-table-rename-fk-deferrable,
/// alter-table-rename-fk-actions, alter-table-rename-composite-fk-parent, and
/// alter-table-rename-pk-and-fk cases, but additionally asserts the child's live column
/// identity (not just the schema text) and that the schema text survives a persisted-file
/// reopen with the corrected wording intact -- both the in-memory candidate table
/// (EmbeddedDatabase.CreateWithRenamedForeignKeyParentColumn) and the row actually written to
/// and re-parsed from the sqlite_schema b-tree must agree.
/// </remarks>
public sealed class ForeignKeyRenamePreservesChildColumnNamesAfterSchemaReplayTests
{
    private const string ExpectedChildSql =
        "CREATE TABLE c(id INTEGER PRIMARY KEY, pid INTEGER, FOREIGN KEY(pid) REFERENCES p(parent_id) DEFERRABLE INITIALLY DEFERRED)";

    [Test]
    public void MemoryDatabase_ChildOwnColumnsAndSchemaTextSurviveParentRename()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE p(id INTEGER PRIMARY KEY);");
        Execute(
            connection,
            "CREATE TABLE c(id INTEGER PRIMARY KEY, pid INTEGER, FOREIGN KEY(pid) REFERENCES p(id) DEFERRABLE INITIALLY DEFERRED);");

        Execute(connection, "ALTER TABLE p RENAME COLUMN id TO parent_id;");

        ReadRows(connection, "SELECT name FROM pragma_table_info('c') ORDER BY cid;")
            .Should()
            .Equal("id", "pid");
        ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'c';")
            .Should()
            .Equal(ExpectedChildSql);

        // The rewritten FK reference must still resolve against the parent's new column, and
        // the child's own "id" must still be usable as its own independent identity.
        Execute(connection, "INSERT INTO p(parent_id) VALUES (1);");
        Execute(connection, "INSERT INTO c(id, pid) VALUES (7, 1);");
        ReadRows(connection, "SELECT id, pid FROM c;").Should().Equal("7|1");
    }

    [Test]
    public void FileDatabase_ChildOwnColumnsAndSchemaTextSurviveReopenAfterParentRename()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fk-rename-replay-{Guid.NewGuid():N}.db");
        try
        {
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                Execute(connection, "CREATE TABLE p(id INTEGER PRIMARY KEY);");
                Execute(
                    connection,
                    "CREATE TABLE c(id INTEGER PRIMARY KEY, pid INTEGER, FOREIGN KEY(pid) REFERENCES p(id) DEFERRABLE INITIALLY DEFERRED);");
                Execute(connection, "ALTER TABLE p RENAME COLUMN id TO parent_id;");
            }

            // Reopening re-parses the persisted sqlite_schema row through
            // ManagedSchemaRowParser.ParseTable -- the only way a schema-text corruption that
            // an in-memory-only check would miss becomes an observable column-identity defect.
            using (var database = EmbeddedDatabase.OpenFile(path))
            using (var connection = database.Connect())
            {
                ReadRows(connection, "SELECT name FROM pragma_table_info('c') ORDER BY cid;")
                    .Should()
                    .Equal("id", "pid");
                ReadRows(connection, "SELECT sql FROM sqlite_schema WHERE name = 'c';")
                    .Should()
                    .Equal(ExpectedChildSql);

                Execute(connection, "INSERT INTO p(parent_id) VALUES (1);");
                Execute(connection, "INSERT INTO c(id, pid) VALUES (7, 1);");
                ReadRows(connection, "SELECT id, pid FROM c;").Should().Equal("7|1");
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var candidate = path + suffix;
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }

    private static string[] ReadRows(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
        {
            var values = new List<string>();
            for (var column = 0; column < statement.ColumnCount; column++)
            {
                var value = statement.GetValue(column);
                values.Add(value.Kind switch
                {
                    SqlValueKind.Null => string.Empty,
                    SqlValueKind.Integer => value.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SqlValueKind.Real => value.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => value.AsText(),
                });
            }

            rows.Add(string.Join("|", values));
        }

        return [.. rows];
    }
}
