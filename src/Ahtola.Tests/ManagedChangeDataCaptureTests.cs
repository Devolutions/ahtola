using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedChangeDataCaptureTests
{
    [Test]
    public void CdcPragmaCreatesThePinnedV2SchemaAndCapturesAutocommitRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)");

        ColumnNames(connection, "PRAGMA capture_data_changes_conn;").Should()
            .Equal("capture_data_changes_conn", "table_name", "version");
        ReadRows(connection, "PRAGMA capture_data_changes_conn;").Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Text("off"), SqlValue.Null, SqlValue.Null);

        Execute(connection, "PRAGMA capture_data_changes_conn('full')");
        ReadRows(connection, "PRAGMA capture_data_changes_conn;").Should()
            .ContainSingle()
            .Which.Should().Equal(
                SqlValue.Text("full"),
                SqlValue.Text("turso_cdc"),
                SqlValue.Text("v2"));

        Execute(connection, "INSERT INTO data VALUES (7, 'seven')");

        var rows = ReadRows(
            connection,
            "SELECT change_id, change_txn_id, change_type, table_name, id, before, after, updates FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Should().Be(SqlValue.Integer(1));
        rows[0][2].Should().Be(SqlValue.Integer(1));
        rows[0][3].Should().Be(SqlValue.Text("data"));
        rows[0][4].Should().Be(SqlValue.Integer(7));
        rows[0][5].Should().Be(SqlValue.Null);
        SqliteRecordCodec.Decode(rows[0][6].AsBlob().Span).Should()
            .Equal(SqlValue.Integer(7), SqlValue.Text("seven"));
        rows[0][7].Should().Be(SqlValue.Null);

        rows[1].Should().Equal(
            SqlValue.Integer(2),
            SqlValue.Integer(1),
            SqlValue.Integer(2),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null);
        ReadRows(connection, "SELECT table_name, version FROM turso_cdc_version").Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Text("turso_cdc"), SqlValue.Text("v2"));
    }

    [Test]
    public void CdcModesUsePinnedBeforeAfterAndFullUpdateRecordShapes()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)");
        Execute(connection, "INSERT INTO data VALUES (1, 'before')");
        Execute(connection, "PRAGMA capture_data_changes_conn('full')");

        Execute(connection, "UPDATE data SET value = 'after' WHERE id = 1");

        var row = ReadRows(
                connection,
                "SELECT before, after, updates FROM turso_cdc WHERE change_type = 0")
            .Should().ContainSingle()
            .Which;
        SqliteRecordCodec.Decode(row[0].AsBlob().Span).Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("before"));
        SqliteRecordCodec.Decode(row[1].AsBlob().Span).Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("after"));
        SqliteRecordCodec.Decode(row[2].AsBlob().Span).Should().Equal(
            SqlValue.Integer(0),
            SqlValue.Integer(1),
            SqlValue.Null,
            SqlValue.Text("after"));
    }

    [Test]
    public void CdcSavepointRollbackDiscardsRowsAndKeepsOneOuterCommitBoundary()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO data VALUES (1)");
        Execute(connection, "SAVEPOINT inner");
        Execute(connection, "INSERT INTO data VALUES (2)");
        Execute(connection, "ROLLBACK TO inner");
        Execute(connection, "COMMIT");

        var rows = ReadRows(
            connection,
            "SELECT change_type, id, change_txn_id FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Null, SqlValue.Integer(1));
    }

    [Test]
    public void FullCdcCapturesSchemaChangesWithoutCapturingItsOwnSetup()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA capture_data_changes_conn('full')");

        Execute(connection, "CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT)");

        var rows = ReadRows(
            connection,
            "SELECT change_type, table_name, after FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(1));
        rows[0][1].Should().Be(SqlValue.Text("sqlite_schema"));
        SqliteRecordCodec.Decode(rows[0][2].AsBlob().Span)[1].Should().Be(SqlValue.Text("products"));
        rows[1][0].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void IdCdcCapturesSchemaRowsAndACommitBoundaryWithoutRowPayloads()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");

        Execute(connection, "CREATE TABLE products(id INTEGER PRIMARY KEY)");

        var rows = ReadRows(
            connection,
            "SELECT change_type, table_name, before, after, updates FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Text("sqlite_schema"),
            SqlValue.Null,
            SqlValue.Null,
            SqlValue.Null);
        rows[1][0].Should().Be(SqlValue.Integer(2));
    }

    [Test]
    public void UpdatingAnIntegerPrimaryKeyEmitsPinnedDeleteAndInsertCdcRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT)");
        Execute(connection, "INSERT INTO data VALUES (1, 'value')");
        Execute(connection, "PRAGMA capture_data_changes_conn('full')");

        Execute(connection, "UPDATE data SET id = 2 WHERE id = 1");

        var rows = ReadRows(
            connection,
            "SELECT change_type, id, before, after FROM turso_cdc WHERE change_type != 2 ORDER BY change_id");
        rows.Should().HaveCount(2);
        rows[0][0].Should().Be(SqlValue.Integer(-1));
        rows[0][1].Should().Be(SqlValue.Integer(1));
        SqliteRecordCodec.Decode(rows[0][2].AsBlob().Span).Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("value"));
        rows[0][3].Should().Be(SqlValue.Null);
        rows[1][0].Should().Be(SqlValue.Integer(1));
        rows[1][1].Should().Be(SqlValue.Integer(2));
        rows[1][2].Should().Be(SqlValue.Null);
        SqliteRecordCodec.Decode(rows[1][3].AsBlob().Span).Should()
            .Equal(SqlValue.Integer(2), SqlValue.Text("value"));
    }

    [Test]
    public void CdcInternalRowsDoNotNotifyThePublicUpdateHookAndPoolResetDisablesCapture()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        var changes = new List<SqliteRowChange>();
        connection.Hooks.UpdateHook = changes.Add;

        Execute(connection, "PRAGMA unstable_capture_data_changes_conn('id')");
        Execute(connection, "INSERT INTO data VALUES (1)");

        changes.Should().ContainSingle()
            .Which.Should().Be(new SqliteRowChange(SqliteChangeOperation.Insert, "main", "data", 1));
        connection.ResetForPooling();
        ReadRows(connection, "PRAGMA capture_data_changes_conn;").Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Text("off"), SqlValue.Null, SqlValue.Null);
    }

    [Test]
    public void CdcTracksNestedTriggerAndForeignKeyMutationsWithoutRecursingIntoItsTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA foreign_keys = ON");
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id) ON DELETE CASCADE)");
        Execute(connection, "CREATE TABLE audit(id INTEGER PRIMARY KEY)");
        Execute(connection, "CREATE TRIGGER parent_audit AFTER INSERT ON parent BEGIN INSERT INTO audit VALUES (NEW.id); END");
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");

        Execute(connection, "INSERT INTO parent VALUES (1)");
        Execute(connection, "INSERT INTO child VALUES (2, 1)");
        Execute(connection, "DELETE FROM parent WHERE id = 1");

        var rows = ReadRows(
            connection,
            "SELECT change_type, table_name, id FROM turso_cdc WHERE change_type != 2 ORDER BY change_id");
        ContainsRow(rows, SqlValue.Integer(1), SqlValue.Text("parent"), SqlValue.Integer(1)).Should().BeTrue();
        ContainsRow(rows, SqlValue.Integer(1), SqlValue.Text("audit"), SqlValue.Integer(1)).Should().BeTrue();
        ContainsRow(rows, SqlValue.Integer(1), SqlValue.Text("child"), SqlValue.Integer(2)).Should().BeTrue();
        ContainsRow(rows, SqlValue.Integer(-1), SqlValue.Text("parent"), SqlValue.Integer(1)).Should().BeTrue();
        ContainsRow(rows, SqlValue.Integer(-1), SqlValue.Text("child"), SqlValue.Integer(2)).Should().BeTrue();
        ReadRows(connection, "SELECT count(*) FROM turso_cdc").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(8));
    }

    [Test]
    public void CdcFailureDoesNotLeakARejectedStatementIntoTheNextTransactionBoundary()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY, value TEXT UNIQUE)");
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");
        Execute(connection, "INSERT INTO data VALUES (1, 'one')");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "INSERT INTO data VALUES (2, 'one')"));
        Execute(connection, "INSERT INTO data VALUES (3, 'three')");

        var rows = ReadRows(
            connection,
            "SELECT change_id, change_txn_id, change_type, id FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(4);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(1), SqlValue.Integer(2), SqlValue.Null);
        rows[2].Should().Equal(SqlValue.Integer(3), SqlValue.Integer(3), SqlValue.Integer(1), SqlValue.Integer(3));
        rows[3].Should().Equal(SqlValue.Integer(4), SqlValue.Integer(3), SqlValue.Integer(2), SqlValue.Null);
    }

    [Test]
    public void ExistingUnversionedCdcTableIsPinnedToV1WithoutCommitRecords()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(
            connection,
            "CREATE TABLE turso_cdc(change_id INTEGER PRIMARY KEY AUTOINCREMENT, change_time INTEGER, change_type INTEGER, table_name TEXT, id, before BLOB, after BLOB, updates BLOB)");

        Execute(connection, "PRAGMA capture_data_changes_conn('after')");
        Execute(connection, "INSERT INTO data VALUES (1)");

        ReadRows(connection, "PRAGMA capture_data_changes_conn;").Should().ContainSingle()
            .Which.Should().Equal(
                SqlValue.Text("after"),
                SqlValue.Text("turso_cdc"),
                SqlValue.Text("v1"));
        var rows = ReadRows(connection, "SELECT * FROM turso_cdc");
        rows.Should().ContainSingle();
        rows[0][2].Should().Be(SqlValue.Integer(1));
        SqliteRecordCodec.Decode(rows[0][6].AsBlob().Span).Should().Equal(SqlValue.Integer(1));
        ReadRows(connection, "SELECT version FROM turso_cdc_version WHERE table_name = 'turso_cdc'")
            .Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("v1"));
    }

    [Test]
    public void DroppingACdcTableCleansUpItsVersionEntry()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");

        Execute(connection, "DROP TABLE turso_cdc");

        ReadRows(connection, "SELECT count(*) FROM turso_cdc_version").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));
    }

    [Test]
    public void ReconfiguringCdcInsideATransactionKeepsThePinnedV2Boundary()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");

        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO data VALUES (1)");
        Execute(connection, "PRAGMA capture_data_changes_conn('full')");
        Execute(connection, "INSERT INTO data VALUES (2)");
        Execute(connection, "COMMIT");

        var rows = ReadRows(
            connection,
            "SELECT change_txn_id, change_type FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(1));
        rows[2].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(2));
    }

    [Test]
    public void FailedCdcProvisioningRollsBackItsNewTargetTable()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE turso_cdc_version(other TEXT)");

        Assert.Throws<EmbeddedSqlException>(() => Execute(connection, "PRAGMA capture_data_changes_conn('id')"));

        ReadRows(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'turso_cdc'").Should()
            .ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(0));
        ReadRows(connection, "PRAGMA capture_data_changes_conn;").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("off"), SqlValue.Null, SqlValue.Null);
    }

    [Test]
    public void ConcurrentMvccCommitPublishesTheSourceAndItsCdcBoundaryAtomically()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("cdc-mvcc.db", fileSystem);
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE data(id INTEGER PRIMARY KEY)");
        ReadRows(connection, "PRAGMA journal_mode = mvcc").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Text("mvcc"));
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");

        Execute(connection, "BEGIN CONCURRENT");
        Execute(connection, "INSERT INTO data VALUES (42)");
        Execute(connection, "COMMIT");

        ReadRows(connection, "SELECT id FROM data").Should().ContainSingle()
            .Which.Should().Equal(SqlValue.Integer(42));
        var rows = ReadRows(
            connection,
            "SELECT change_type, id, change_txn_id FROM turso_cdc ORDER BY change_id");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(SqlValue.Integer(1), SqlValue.Integer(42), SqlValue.Integer(1));
        rows[1].Should().Equal(SqlValue.Integer(2), SqlValue.Null, SqlValue.Integer(1));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static string[] ColumnNames(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var names = new string[statement.GetColumnCount()];
        for (var index = 0; index < names.Length; index++)
            names[index] = statement.GetColumnName(index);
        return names;
    }

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
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

    private static bool ContainsRow(IEnumerable<SqlValue[]> rows, params SqlValue[] expected)
        => rows.Any(row => row.SequenceEqual(expected));
}
