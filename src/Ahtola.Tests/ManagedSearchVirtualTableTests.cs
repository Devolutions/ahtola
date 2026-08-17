using AwesomeAssertions;
using Ahtola.Core;

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
}
