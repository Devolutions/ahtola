using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

// SEQUENCE parity with Turso v0.8.0-pre.7: CREATE/DROP validation diagnostics, the disk-only
// watermark semantics (nextval/setval read and rewrite the backing row), per-connection currval,
// and transaction durability (a rolled-back allocation can be re-emitted).
public class ManagedSequenceTests
{
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

    private static long Scalar(EmbeddedConnection connection, string sql)
        => Query(connection, sql)[0][0].AsInteger();

    private static string CaptureError(EmbeddedConnection connection, string sql)
    {
        try
        {
            using var statement = connection.Prepare(sql);
            statement.Step();
        }
        catch (EmbeddedSqlException exception)
        {
            return exception.Message;
        }

        return "no error";
    }

    private static EmbeddedConnection Connect(EmbeddedDatabase? database = null)
    {
        database ??= new EmbeddedDatabase();
        return database.Connect();
    }

    [Test]
    public void CreateAndNextValProduceAscendingValues()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Scalar(connection, "SELECT nextval('s')").Should().Be(1);
        Scalar(connection, "SELECT nextval('s')").Should().Be(2);
        Scalar(connection, "SELECT nextval('s')").Should().Be(3);
    }

    [Test]
    public void OptionsControlStartIncrementAndBounds()
    {
        using var connection = Connect();
        Execute(
            connection,
            "CREATE SEQUENCE s START WITH 10 INCREMENT BY 5 MINVALUE 5 MAXVALUE 20");
        Scalar(connection, "SELECT nextval('s')").Should().Be(10);
        Scalar(connection, "SELECT nextval('s')").Should().Be(15);
        Scalar(connection, "SELECT nextval('s')").Should().Be(20);
    }

    [Test]
    public void DescendingSequenceCountsDown()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s START WITH -1 INCREMENT BY -1 MINVALUE -5 MAXVALUE -1");
        Scalar(connection, "SELECT nextval('s')").Should().Be(-1);
        Scalar(connection, "SELECT nextval('s')").Should().Be(-2);
    }

    [Test]
    public void CycleWrapsToMinValue()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s START WITH 2 MAXVALUE 2 CYCLE");
        Scalar(connection, "SELECT nextval('s')").Should().Be(2);
        Scalar(connection, "SELECT nextval('s')").Should().Be(1);
    }

    [Test]
    public void ExhaustionWithoutCycleFailsWithTursoDiagnostic()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s START WITH 2 MAXVALUE 2 NO CYCLE");
        Scalar(connection, "SELECT nextval('s')").Should().Be(2);
        CaptureError(connection, "SELECT nextval('s')")
            .Should().Be("nextval: reached maximum value of sequence \"s\"");
    }

    [Test]
    public void SetValRewritesTheWatermark()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Scalar(connection, "SELECT nextval('s')").Should().Be(1);
        Scalar(connection, "SELECT setval('s', 10)").Should().Be(10);
        Scalar(connection, "SELECT nextval('s')").Should().Be(11);
    }

    [Test]
    public void SetValWithIsCalledFalseReturnsSameValueOnNextVal()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Scalar(connection, "SELECT setval('s', 7, false)").Should().Be(7);
        Scalar(connection, "SELECT nextval('s')").Should().Be(7);
    }

    [Test]
    public void SetValRejectsOutOfBoundsValue()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s MAXVALUE 5");
        CaptureError(connection, "SELECT setval('s', 6)")
            .Should().Be("setval: value 6 is out of bounds for sequence \"s\" (1..5)");
    }

    [Test]
    public void CurrValReportsSessionValue()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Scalar(connection, "SELECT nextval('s')");
        Scalar(connection, "SELECT currval('s')").Should().Be(1);
    }

    [Test]
    public void CurrValBeforeAnyCallIsNotDefinedInThisSession()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        CaptureError(connection, "SELECT currval('s')")
            .Should().Be("currval of sequence \"s\" is not yet defined in this session");
    }

    [Test]
    public void CurrValIsPerConnection()
    {
        var database = new EmbeddedDatabase();
        using var first = Connect(database);
        using var second = Connect(database);
        Execute(first, "CREATE SEQUENCE s");
        Scalar(first, "SELECT nextval('s')");
        CaptureError(second, "SELECT currval('s')")
            .Should().Be("currval of sequence \"s\" is not yet defined in this session");
    }

    [Test]
    public void MissingSequenceIsRejectedWithTursoDiagnostic()
    {
        using var connection = Connect();
        CaptureError(connection, "SELECT nextval('nope')")
            .Should().Be("sequence \"nope\" does not exist");
    }

    [Test]
    public void CreateRejectsReservedAutoincrementNamespace()
    {
        using var connection = Connect();
        CaptureError(connection, "CREATE SEQUENCE __turso_internal_autoincrement_x")
            .Should().Be(
                "sequence name \"__turso_internal_autoincrement_x\" is reserved for internal AUTOINCREMENT use");
    }

    [Test]
    public void CreateRejectsZeroIncrement()
    {
        using var connection = Connect();
        CaptureError(connection, "CREATE SEQUENCE s INCREMENT BY 0")
            .Should().Be("INCREMENT must not be zero");
    }

    [Test]
    public void CreateRejectsInvertedBounds()
    {
        using var connection = Connect();
        CaptureError(connection, "CREATE SEQUENCE s MINVALUE 10 MAXVALUE 5")
            .Should().Be("MINVALUE (10) must be less than MAXVALUE (5)");
    }

    [Test]
    public void CreateRejectsStartOutsideBounds()
    {
        using var connection = Connect();
        CaptureError(connection, "CREATE SEQUENCE s START WITH 0 MINVALUE 1")
            .Should().Be("START value (0) cannot be less than MINVALUE (1)");
        CaptureError(connection, "CREATE SEQUENCE s2 START WITH 10 MAXVALUE 5")
            .Should().Be("START value (10) cannot be greater than MAXVALUE (5)");
    }

    [Test]
    public void CreateWithoutIfNotExistsRejectsDuplicate()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        CaptureError(connection, "CREATE SEQUENCE s")
            .Should().Be("sequence \"s\" already exists");
    }

    [Test]
    public void CreateIfNotExistsIsANoOpForDuplicates()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s START WITH 5");
        Execute(connection, "CREATE SEQUENCE IF NOT EXISTS s");
        Scalar(connection, "SELECT nextval('s')").Should().Be(5);
    }

    [Test]
    public void DropRemovesTheSequenceAndCurrval()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Scalar(connection, "SELECT nextval('s')");
        Execute(connection, "DROP SEQUENCE s");
        CaptureError(connection, "SELECT nextval('s')")
            .Should().Be("sequence \"s\" does not exist");
        // A recreated same-name sequence does not inherit the stale session currval.
        Execute(connection, "CREATE SEQUENCE s");
        CaptureError(connection, "SELECT currval('s')")
            .Should().Be("currval of sequence \"s\" is not yet defined in this session");
    }

    [Test]
    public void DropMissingSequenceIsRejectedAndIfExistsIsANoOp()
    {
        using var connection = Connect();
        CaptureError(connection, "DROP SEQUENCE nope")
            .Should().Be("sequence \"nope\" does not exist");
        Execute(connection, "DROP SEQUENCE IF EXISTS nope");
    }

    [Test]
    public void NamesAreCaseInsensitive()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE MySeq");
        Scalar(connection, "SELECT nextval('myseq')").Should().Be(1);
        Scalar(connection, "SELECT nextval('MYSEQ')").Should().Be(2);
    }

    [Test]
    public void RolledBackAllocationCanBeReEmitted()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Execute(connection, "BEGIN");
        Scalar(connection, "SELECT nextval('s')").Should().Be(1);
        Scalar(connection, "SELECT nextval('s')").Should().Be(2);
        Execute(connection, "ROLLBACK");
        // The backing-table watermark is ordinary table state: rollback discards it, so the next
        // allocation re-emits the value — Turso's documented behavior (no burned values).
        Scalar(connection, "SELECT nextval('s')").Should().Be(1);
    }

    [Test]
    public void CommittedAllocationAdvancesAcrossConnections()
    {
        var database = new EmbeddedDatabase();
        using var first = Connect(database);
        using var second = Connect(database);
        Execute(first, "CREATE SEQUENCE s");
        Scalar(first, "SELECT nextval('s')").Should().Be(1);
        Scalar(first, "SELECT nextval('s')").Should().Be(2);
        Scalar(second, "SELECT nextval('s')").Should().Be(3);
    }

    [Test]
    public void SequenceSurvivesReopenOnFileBackedDatabase()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ahtola-sequence-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = EmbeddedDatabase.OpenFile(path).Connect())
            {
                Execute(connection, "CREATE SEQUENCE s START WITH 100 INCREMENT BY 10");
                Scalar(connection, "SELECT nextval('s')").Should().Be(100);
            }

            using (var reopened = EmbeddedDatabase.OpenFile(path).Connect())
            {
                Scalar(reopened, "SELECT nextval('s')").Should().Be(110);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NextValInsideInsertFeedsColumnValues()
    {
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s");
        Execute(connection, "CREATE TABLE t(id INTEGER, who TEXT)");
        Execute(connection, "INSERT INTO t VALUES (nextval('s'), 'a'), (nextval('s'), 'b')");
        var rows = Query(connection, "SELECT id FROM t ORDER BY id");
        rows.Count.Should().Be(2);
        rows[0][0].AsInteger().Should().Be(1);
        rows[1][0].AsInteger().Should().Be(2);
    }

    [Test]
    public void SequenceBackingTableIsAnOrdinarySchemaTable()
    {
        // Turso adopts every __turso_internal_seq_* backing table as an ordinary BTreeTable, so the
        // descriptor row is visible to ordinary queries exactly as upstream's is.
        using var connection = Connect();
        Execute(connection, "CREATE SEQUENCE s START WITH 5 INCREMENT BY 2");
        var rows = Query(connection, "SELECT value, is_called FROM __turso_internal_seq_s");
        rows.Count.Should().Be(1);
        rows[0][0].AsInteger().Should().Be(5);
        rows[0][1].AsInteger().Should().Be(0);
    }
}
