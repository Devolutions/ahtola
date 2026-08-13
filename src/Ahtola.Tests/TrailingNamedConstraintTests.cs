using AwesomeAssertions;
using Ahtola.Core;

namespace Ahtola.Tests;

public class TrailingNamedConstraintTests
{
    [Test]
    public void TrailingColumnConstraintNameIsIgnored()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE data(id INTEGER CONSTRAINT data_pk PRIMARY KEY, value CONSTRAINT ignored);");
        Execute(connection, "INSERT INTO data VALUES (1, 'first');");
        Execute(connection, "INSERT OR IGNORE INTO data VALUES (1, 'second');");

        ReadRows(connection, "SELECT id, value FROM data;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("first"));
    }

    [Test]
    public void AlterTableAddColumnAcceptsTrailingConstraintName()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE data(id INTEGER);");
        Execute(connection, "ALTER TABLE data ADD COLUMN value CONSTRAINT ignored;");
        Execute(connection, "INSERT INTO data VALUES (1, 'stored');");

        ReadRows(connection, "SELECT id, value FROM data;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(1), SqlValue.Text("stored"));
    }

    [Test]
    public void TrailingTableConstraintNameIsIgnored()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Execute(connection, "CREATE TABLE data(id INTEGER, CONSTRAINT ignored);");
        Execute(connection, "INSERT INTO data VALUES (1);");

        ReadRows(connection, "SELECT id FROM data;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Integer(1));
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    private static IReadOnlyList<SqlValue[]> ReadRows(EmbeddedConnection connection, string sql)
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
}
