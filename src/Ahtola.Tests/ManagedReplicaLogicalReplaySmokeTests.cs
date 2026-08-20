using Ahtola.Core;
using AwesomeAssertions;

namespace Ahtola.Tests;

/// <summary>Smoke-tests SQL behaviors the managed replica logical replay engine depends on.</summary>
public sealed class ManagedReplicaLogicalReplaySmokeTests
{
    [Test]
    public void PragmaTableInfoTableValuedFunctionAcceptsABoundParameter()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();
        Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)");

        using var statement = connection.Prepare("SELECT cid, name, type, pk FROM pragma_table_info(?)");
        statement.Bind(1, SqlValue.Text("t"));
        var rows = new List<(long Cid, string Name, string Type, long Pk)>();
        while (statement.Step() == StatementStepResult.Row)
        {
            rows.Add((
                statement.GetValue(0).AsInteger(),
                statement.GetValue(1).AsText(),
                statement.GetValue(2).AsText(),
                statement.GetValue(3).AsInteger()));
        }

        rows.Should().HaveCount(2);
        rows[0].Should().Be((0L, "id", "INTEGER", 1L));
        rows[1].Should().Be((1L, "name", "TEXT", 0L));
    }

    [Test]
    public void PragmaTableInfoOnMissingTableReturnsZeroRows()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();

        using var statement = connection.Prepare("SELECT cid, name, type, pk FROM pragma_table_info(?)");
        statement.Bind(1, SqlValue.Text("missing"));
        statement.Step().Should().Be(StatementStepResult.Done);
    }

    [Test]
    public void InsertOnConflictDoUpdateSetReplaysAFullRowImage()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();
        Exec(connection, "CREATE TABLE t(x TEXT PRIMARY KEY, y TEXT)");
        Exec(connection, "INSERT INTO t(x, y) VALUES ('a', 'old')");

        using (var statement = connection.Prepare(
            "INSERT INTO t(x, y) VALUES (?, ?) ON CONFLICT(x) DO UPDATE SET x = excluded.x, y = excluded.y"))
        {
            statement.Bind(1, SqlValue.Text("a"));
            statement.Bind(2, SqlValue.Text("new"));
            statement.Step();
        }

        using var read = connection.Prepare("SELECT y FROM t WHERE x = 'a'");
        read.Step().Should().Be(StatementStepResult.Row);
        read.GetValue(0).AsText().Should().Be("new");
    }

    [Test]
    public void InsertOrReplaceCanSetTheImplicitRowidColumnExplicitly()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();
        Exec(connection, "CREATE TABLE t(a TEXT, b TEXT)");

        using (var statement = connection.Prepare("INSERT OR REPLACE INTO t(a, b, \"rowid\") VALUES (?, ?, ?)"))
        {
            statement.Bind(1, SqlValue.Text("va"));
            statement.Bind(2, SqlValue.Text("vb"));
            statement.Bind(3, SqlValue.Integer(42));
            statement.Step();
        }

        using var read = connection.Prepare("SELECT rowid, a, b FROM t");
        read.Step().Should().Be(StatementStepResult.Row);
        read.GetValue(0).AsInteger().Should().Be(42);
    }

    [Test]
    public void AlterTableAddColumnIsVisibleThroughPragmaTableInfo()
    {
        using var database = ManagedDatabaseAdapter.Open(":memory:");
        var connection = database.Connect();
        Exec(connection, "CREATE TABLE t(x INTEGER PRIMARY KEY)");
        Exec(connection, "ALTER TABLE t ADD COLUMN note TEXT");

        using var statement = connection.Prepare("SELECT name FROM pragma_table_info(?)");
        statement.Bind(1, SqlValue.Text("t"));
        var names = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            names.Add(statement.GetValue(0).AsText());
        names.Should().Equal("x", "note");
    }

    private static void Exec(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step();
    }
}
