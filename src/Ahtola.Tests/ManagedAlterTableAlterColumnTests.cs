using AwesomeAssertions;
using ManagedSqliteConnection = Ahtola.Data.Sqlite.SqliteConnection;

namespace Ahtola.Tests;

[NonParallelizable]
public sealed class ManagedAlterTableAlterColumnTests
{
    [Test]
    public void AlterColumnReplacingAnAutoincrementRowidAliasClearsItsSequence()
    {
        using var connection = new ManagedSqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        Execute(connection, """
            CREATE TABLE t(a INTEGER PRIMARY KEY AUTOINCREMENT, b TEXT);
            INSERT INTO t(b) VALUES('x'),('y');
            ALTER TABLE t ALTER COLUMN a TO a2 TEXT;
            """);

        Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_sequence WHERE name='t';").Should().Be(0);
        Scalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE name='t';")
            .Should().Be("CREATE TABLE t (a2 TEXT, b TEXT)");

        Execute(connection, "INSERT INTO t(b) VALUES('z');");

        Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_sequence WHERE name='t';").Should().Be(0);
        ReadRows(connection, "SELECT a2, b FROM t ORDER BY rowid;")
            .Should()
            .Equal("1\u001fx", "2\u001fy", "<null>\u001fz");
    }

    private static void Execute(ManagedSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(ManagedSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static IReadOnlyList<string> ReadRows(ManagedSqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "\u001f",
                Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString())));
        }

        return rows;
    }
}
