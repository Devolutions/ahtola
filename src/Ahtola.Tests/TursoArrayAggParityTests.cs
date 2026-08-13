using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class TursoArrayAggParityTests
{
    [Test]
    public void ArrayAggReturnsTursoCompatibleRecordBlobIncludingNulls()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE values_table(value);");
        Execute(
            connection,
            "INSERT INTO values_table VALUES (NULL), (7), (2.5), ('text'), (X'CAFE');");

        var result = Scalar(connection, "SELECT array_agg(value) FROM values_table;");

        result.Kind.Should().Be(SqlValueKind.Blob);
        SqliteRecordCodec.Decode(result.AsBlob().Span).Should().Equal(
            SqlValue.Null,
            SqlValue.Integer(7),
            SqlValue.Real(2.5),
            SqlValue.Text("text"),
            SqlValue.Blob([0xCA, 0xFE]));
    }

    [Test]
    public void ArrayAggReturnsNullForAnEmptyGroup()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Scalar(connection, "SELECT array_agg(value) FROM (SELECT 1 AS value WHERE 0);")
            .Should().Be(SqlValue.Null);
    }

    [Test]
    public void ArrayAggSupportsAggregateModifiersAndWindowFrames()
    {
        using var connection = new EmbeddedDatabase().Connect();
        Execute(connection, "CREATE TABLE values_table(group_id INTEGER, value INTEGER);");
        Execute(connection, "INSERT INTO values_table VALUES (1, 2), (1, 2), (1, NULL), (2, 3);");

        var distinct = Scalar(
            connection,
            "SELECT array_agg(DISTINCT value) FILTER (WHERE group_id = 1) FROM values_table;");
        SqliteRecordCodec.Decode(distinct.AsBlob().Span).Should().Equal(
            SqlValue.Integer(2),
            SqlValue.Null);

        var window = ReadRows(
            connection,
            """
            SELECT array_agg(value) OVER (
                ORDER BY value ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
            FROM values_table
            WHERE group_id = 1
            ORDER BY value;
            """);
        window.Select(row => SqliteRecordCodec.Decode(row[0].AsBlob().Span))
            .Should().SatisfyRespectively(
                values => values.Should().Equal(SqlValue.Null),
                values => values.Should().Equal(SqlValue.Null, SqlValue.Integer(2)),
                values => values.Should().Equal(SqlValue.Null, SqlValue.Integer(2), SqlValue.Integer(2)));
    }

    [Test]
    public void ArrayAggViewSurvivesFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();
        const string path = "array-agg-view.db";
        using (var database = EmbeddedDatabase.OpenFile(path, fileSystem))
        using (var connection = database.Connect())
        {
            Execute(connection, "CREATE TABLE values_table(value);");
            Execute(connection, "INSERT INTO values_table VALUES (1), (NULL), ('three');");
            Execute(connection, "CREATE VIEW value_array AS SELECT array_agg(value) AS values FROM values_table;");
        }

        using var reopened = EmbeddedDatabase.OpenFile(path, fileSystem, readOnly: true);
        using var connectionAfterReopen = reopened.Connect();
        var result = Scalar(connectionAfterReopen, "SELECT values FROM value_array;");

        SqliteRecordCodec.Decode(result.AsBlob().Span).Should().Equal(
            SqlValue.Integer(1),
            SqlValue.Null,
            SqlValue.Text("three"));
    }

    [Test]
    public void ArrayAggReportsItsOwnArityError()
    {
        using var connection = new EmbeddedDatabase().Connect();

        Assert.Throws<EmbeddedSqlException>(() => Scalar(connection, "SELECT array_agg();"))
            .Message.Should().Be("wrong number of arguments to function array_agg()");
    }

    private static SqlValue Scalar(EmbeddedConnection connection, string sql)
        => ReadRows(connection, sql).Single()[0];

    private static List<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
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

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }
}
