using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// v0.8.1 audit gaps in change data capture: the record decoders
/// <c>table_columns_json_array</c> and <c>bin_record_json_object</c>, and the
/// <c>conn_txn_id</c> get-or-set transaction id (Turso vdbe/execute.rs <c>ScalarFunc</c>).
/// </summary>
public sealed class TursoV081ChangeDataCaptureGapTests
{
    [Test]
    public void CdcDecodersTurnCapturedRecordsIntoJson()
    {
        using var database = EmbeddedDatabase.OpenFile("cdc-decode.db", new InMemoryFileSystem());
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT, score REAL, note)");
        Execute(connection, "PRAGMA capture_data_changes_conn('full')");
        Execute(connection, "INSERT INTO t VALUES (1, 'a\"b', 2.5, NULL)");
        Execute(connection, "UPDATE t SET name = 'c' WHERE id = 1");

        var columns = Scalar(connection, "SELECT table_columns_json_array('t')");
        columns.AsText().Should().Be("[\"id\",\"name\",\"score\",\"note\"]");
        columns.IsJson.Should().BeTrue();

        var images = ReadRows(
            connection,
            "SELECT bin_record_json_object(table_columns_json_array('t'), before), "
            + "bin_record_json_object(table_columns_json_array('t'), after) "
            + "FROM turso_cdc WHERE table_name = 't' ORDER BY change_id");
        images.Should().HaveCount(2);
        images[0][0].Kind.Should().Be(SqlValueKind.Null, "an insert has no before image");
        images[0][1].AsText().Should().Be("{\"id\":1,\"name\":\"a\\\"b\",\"score\":2.5,\"note\":null}");
        images[1][0].AsText().Should().Be("{\"id\":1,\"name\":\"a\\\"b\",\"score\":2.5,\"note\":null}");
        images[1][1].AsText().Should().Be("{\"id\":1,\"name\":\"c\",\"score\":2.5,\"note\":null}");
    }

    [Test]
    public void CdcDecodersReportTursoErrors()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(a, b)");

        var missing = () => Scalar(connection, "SELECT table_columns_json_array('nope')");
        missing.Should().Throw<EmbeddedSqlException>().WithMessage("table_columns_json_array: table nope doesn't exists");
        var notText = () => Scalar(connection, "SELECT table_columns_json_array(1)");
        notText.Should().Throw<EmbeddedSqlException>().WithMessage("table_columns_json_array: function argument must be of type TEXT");

        Scalar(connection, "SELECT bin_record_json_object('[\"a\"]', NULL)").Kind.Should().Be(SqlValueKind.Null);
        var wrongType = () => Scalar(connection, "SELECT bin_record_json_object('[\"a\"]', 'x')");
        wrongType.Should().Throw<EmbeddedSqlException>().WithMessage("*must be of type TEXT and BLOB correspondingly");

        var record = Convert.ToHexString(SqliteRecordCodec.Encode([SqlValue.Integer(1)]));
        var tooFew = () => Scalar(connection, $"SELECT bin_record_json_object('[\"a\",\"b\"]', x'{record}')");
        tooFew.Should().Throw<EmbeddedSqlException>().WithMessage("*fewer columns than specified*");
        var blobRecord = Convert.ToHexString(SqliteRecordCodec.Encode([SqlValue.Blob(new byte[] { 1 })]));
        var blob = () => Scalar(connection, $"SELECT bin_record_json_object('[\"a\"]', x'{blobRecord}')");
        blob.Should().Throw<EmbeddedSqlException>().WithMessage("*BLOB values stored in binary record is not supported");
    }

    [Test]
    public void ConnTxnIdKeepsTheFirstCandidateForTheTransaction()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        // Autocommit: the id lasts for the statement.
        ReadRows(connection, "SELECT conn_txn_id(5), conn_txn_id(6)")[0]
            .Should().Equal(SqlValue.Integer(5), SqlValue.Integer(5));
        Scalar(connection, "SELECT conn_txn_id(7)").Should().Be(SqlValue.Integer(7));

        // Explicit transaction: the id lasts until COMMIT or ROLLBACK.
        Execute(connection, "BEGIN");
        Scalar(connection, "SELECT conn_txn_id(10)").Should().Be(SqlValue.Integer(10));
        Scalar(connection, "SELECT conn_txn_id(11)").Should().Be(SqlValue.Integer(10));
        Execute(connection, "COMMIT");
        Scalar(connection, "SELECT conn_txn_id(12)").Should().Be(SqlValue.Integer(12));

        Execute(connection, "BEGIN");
        Scalar(connection, "SELECT conn_txn_id(20)").Should().Be(SqlValue.Integer(20));
        Execute(connection, "ROLLBACK");
        Execute(connection, "BEGIN");
        Scalar(connection, "SELECT conn_txn_id(21)").Should().Be(SqlValue.Integer(21));
        Execute(connection, "COMMIT");

        // A non-integer candidate is -1, as in Turso.
        Scalar(connection, "SELECT conn_txn_id('x')").Should().Be(SqlValue.Integer(-1));
    }

    [Test]
    public void ConnTxnIdIsTheIdCaptureStampsOnChangeRows()
    {
        using var database = EmbeddedDatabase.OpenFile("cdc-txn.db", new InMemoryFileSystem());
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(x)");
        Execute(connection, "PRAGMA capture_data_changes_conn('id')");
        Execute(connection, "BEGIN");
        Execute(connection, "INSERT INTO t VALUES (1)");
        var id = Scalar(connection, "SELECT conn_txn_id(-5)");
        Execute(connection, "INSERT INTO t VALUES (2)");
        Execute(connection, "COMMIT");

        var stamped = ReadRows(connection, "SELECT DISTINCT change_txn_id FROM turso_cdc WHERE table_name = 't'");
        stamped.Should().ContainSingle().Which[0].Should().Be(id);
    }

    [Test]
    public void CaptureKeepsRecordingAcrossJournalModeChanges()
    {
        // Turso 0.8 rejects a journal_mode change while capture is on, because its capture
        // would silently stop. Ahtola's capture writes through the catalog in every mode, so
        // the change is allowed and capture continues.
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"cdc-journal-{Guid.NewGuid():N}.db");
        try
        {
            using var database = EmbeddedDatabase.OpenFile(path, new PhysicalFileSystem());
            using var connection = database.Connect();
            Execute(connection, "CREATE TABLE t(x)");
            Execute(connection, "PRAGMA capture_data_changes_conn('full')");
            Execute(connection, "INSERT INTO t VALUES (1)");
            Execute(connection, "PRAGMA journal_mode = 'mvcc'");
            Execute(connection, "INSERT INTO t VALUES (2)");
            Execute(connection, "PRAGMA journal_mode = 'wal'");
            Execute(connection, "INSERT INTO t VALUES (3)");

            Scalar(connection, "SELECT count(*) FROM turso_cdc WHERE table_name = 't'").Should().Be(SqlValue.Integer(3));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Should().ContainSingle().Subject[0];

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
}
