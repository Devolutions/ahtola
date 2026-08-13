using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

public sealed class ManagedDefaultValuesTests
{
    [Test]
    public void DefaultValuesApplyDefaultsComputeGeneratedColumnsFireTriggersAndReturnTheInsertedRow()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(
            connection,
            "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT DEFAULT 'new', quantity INTEGER DEFAULT 3, doubled AS (quantity * 2) VIRTUAL);");
        Execute(connection, "CREATE TABLE audit(event TEXT DEFAULT 'inserted');");
        Execute(
            connection,
            "CREATE TRIGGER item_insert AFTER INSERT ON items "
                + "BEGIN INSERT INTO audit VALUES ('inserted'); END;");

        using var insert = connection.Prepare(
            "INSERT INTO items DEFAULT VALUES RETURNING id, label, quantity, doubled;");
        insert.Step().Should().Be(StatementStepResult.Row);
        insert.GetValue(0).Should().Be(SqlValue.Integer(1));
        insert.GetValue(1).Should().Be(SqlValue.Text("new"));
        insert.GetValue(2).Should().Be(SqlValue.Integer(3));
        insert.GetValue(3).Should().Be(SqlValue.Integer(6));
        insert.Step().Should().Be(StatementStepResult.Done);
        insert.RowsAffected.Should().Be(1);
        connection.LastInsertRowId.Should().Be(1);

        ReadRows(connection, "SELECT event FROM audit;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Text("inserted"));
    }

    [Test]
    public void DefaultValuesHonorNotNullAndRejectColumnLists()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE required_items(value TEXT NOT NULL);");

        Action missingRequiredValue = () => Execute(connection, "INSERT INTO required_items DEFAULT VALUES;");
        missingRequiredValue.Should()
            .Throw<EmbeddedSqlException>()
            .WithMessage("*NOT NULL constraint failed: required_items.value*");
        ReadRows(connection, "SELECT * FROM required_items;").Should().BeEmpty();

        Execute(connection, "CREATE TABLE optional_items(value TEXT DEFAULT 'default');");
        Action columnList = () => Execute(connection, "INSERT INTO optional_items(value) DEFAULT VALUES;");
        columnList.Should()
            .Throw<EmbeddedSqlException>()
            .WithMessage("*DEFAULT VALUES cannot be used with a column list*");
        ReadRows(connection, "SELECT * FROM optional_items;").Should().BeEmpty();
    }

    [Test]
    public void DefaultValuesPersistAcrossFileReopen()
    {
        var fileSystem = new InMemoryFileSystem();

        using (var database = EmbeddedDatabase.OpenFile("default-values.db", fileSystem))
        using (var connection = database.Connect())
        {
            Execute(
                connection,
                "CREATE TABLE items(id INTEGER PRIMARY KEY, label TEXT DEFAULT 'persisted', quantity INTEGER DEFAULT 4, doubled AS (quantity * 2) VIRTUAL);");
            Execute(connection, "INSERT INTO items DEFAULT VALUES;");
        }

        using var reopened = EmbeddedDatabase.OpenFile("default-values.db", fileSystem);
        using var reopenedConnection = reopened.Connect();
        ReadRows(reopenedConnection, "SELECT id, label, quantity, doubled FROM items;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(
                SqlValue.Integer(1),
                SqlValue.Text("persisted"),
                SqlValue.Integer(4),
                SqlValue.Integer(8));
    }

    [TestCase("bare_identifier", "bare_identifier")]
    [TestCase("[bracketed identifier]", "bracketed identifier")]
    [TestCase("\"quoted identifier\"", "quoted identifier")]
    public void BareIdentifierDefaultsAreStoredAsText(string defaultExpression, string expected)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, $"CREATE TABLE defaults(value TEXT DEFAULT {defaultExpression});");
        Execute(connection, "INSERT INTO defaults DEFAULT VALUES;");

        ReadRows(connection, "SELECT value FROM defaults;")
            .Should()
            .ContainSingle()
            .Which
            .Should()
            .Equal(SqlValue.Text(expected));
    }

    [Test]
    public void ParenthesizedIdentifierDefaultsRemainNonConstantExpressions()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();

        Action create = () => Execute(connection, "CREATE TABLE defaults(value TEXT DEFAULT (identifier));");

        create.Should()
            .Throw<EmbeddedSqlException>()
            .WithMessage("default value of column [value] is not constant");
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
