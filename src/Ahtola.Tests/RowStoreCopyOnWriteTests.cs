using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Storage;

namespace Ahtola.Tests;

/// <summary>
/// Catalog clones share their row lists copy-on-write and their row arrays outright (see
/// <c>RowStore</c>). These tests pin the isolation that sharing has to preserve, including the two
/// sites that used to patch a stored row array in place.
/// </summary>
public sealed class RowStoreCopyOnWriteTests
{
    [Test]
    public void CloneSharesRowsUntilEitherSideMutates()
    {
        var first = new SqlValue[] { SqlValue.Integer(1) };
        var second = new SqlValue[] { SqlValue.Integer(2) };
        var source = new RowStore { first, second };
        var clone = new RowStore();

        clone.ReplaceContentsPreservingRevision(source);

        clone.Revision.Should().Be(source.Revision);
        clone.LineageId.Should().Be(source.LineageId);
        clone[0].Should().BeSameAs(first);

        clone.Add([SqlValue.Integer(3)]);
        clone.RemoveAt(0);
        source.Should().HaveCount(2);
        source[0].Should().BeSameAs(first);

        source[1] = [SqlValue.Integer(20)];
        clone[0].Should().BeSameAs(second);

        source.Clear();
        clone.Should().HaveCount(2);
    }

    [Test]
    public void ReplaceRowPreservingRevisionLeavesTheCloneAndRevisionUntouched()
    {
        var original = new SqlValue[] { SqlValue.Integer(1) };
        var source = new RowStore { original };
        var clone = new RowStore();
        clone.ReplaceContentsPreservingRevision(source);
        var revision = source.Revision;

        source.ReplaceRowPreservingRevision(0, [SqlValue.Integer(9)]);

        source.Revision.Should().Be(revision);
        source[0][0].Should().Be(SqlValue.Integer(9));
        clone[0].Should().BeSameAs(original);
    }

    [Test]
    public void FailedStatementInsideTransactionLeavesEarlierRowsAndNoPartialRows()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT UNIQUE);");
        Execute(connection, "CREATE TABLE other(x);");
        Execute(connection, "INSERT INTO other VALUES (1);");
        Execute(connection, "BEGIN;");
        Execute(connection, "INSERT INTO t VALUES (1, 'a');");

        var failing = () => Execute(connection, "INSERT INTO t VALUES (2, 'b'), (3, 'a');");

        failing.Should().Throw<EmbeddedSqlException>().WithMessage("*UNIQUE*");
        Execute(connection, "INSERT INTO t VALUES (4, 'c');");
        Execute(connection, "COMMIT;");
        Query(connection, "SELECT id FROM t ORDER BY id;").Should().Equal(1L, 4L);
        Query(connection, "SELECT x FROM other;").Should().Equal(1L);
    }

    [Test]
    public void RolledBackAutoIncrementRenameRestoresSqliteSequence()
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        Execute(connection, "CREATE TABLE a(id INTEGER PRIMARY KEY AUTOINCREMENT, v);");
        Execute(connection, "INSERT INTO a(v) VALUES ('x');");
        Execute(connection, "BEGIN;");
        Execute(connection, "ALTER TABLE a RENAME TO b;");
        QueryText(connection, "SELECT name FROM sqlite_sequence;").Should().Equal("b");

        Execute(connection, "ROLLBACK;");

        QueryText(connection, "SELECT name FROM sqlite_sequence;").Should().Equal("a");
        Execute(connection, "INSERT INTO a(v) VALUES ('y');");
        Query(connection, "SELECT id FROM a ORDER BY id;").Should().Equal(1L, 2L);
    }

    [Test]
    public void IncrementalBlobWriteRolledBackToSavepointRestoresTheStoredValue()
    {
        var fileSystem = new InMemoryFileSystem();
        using var database = EmbeddedDatabase.OpenFile("row-store-cow-blob.db", fileSystem);
        using var embedded = database.Connect();
        using var adapter = ManagedDatabaseAdapter.FromConnection(embedded);
        var connection = adapter.Connection;
        Execute(connection, "CREATE TABLE data(value BLOB)");
        Execute(connection, "INSERT INTO data(rowid, value) VALUES (1, X'010203')");
        Execute(connection, "BEGIN");
        Execute(connection, "SAVEPOINT s");

        using (var blob = connection.OpenBlob("main", "data", "value", 1))
            blob.Write(1, [9]);

        ReadBlob(connection, "SELECT value FROM data WHERE rowid = 1").Should().Equal(1, 9, 3);
        Execute(connection, "ROLLBACK TO s");
        ReadBlob(connection, "SELECT value FROM data WHERE rowid = 1").Should().Equal(1, 2, 3);
        Execute(connection, "COMMIT");
        ReadBlob(connection, "SELECT value FROM data WHERE rowid = 1").Should().Equal(1, 2, 3);
    }

    private static void Execute(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static void Execute(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        while (statement.Step() == StatementStepResult.Row)
        {
        }
    }

    private static List<long> Query(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<long>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsInteger());
        return values;
    }

    private static List<string> QueryText(EmbeddedConnection connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        var values = new List<string>();
        while (statement.Step() == StatementStepResult.Row)
            values.Add(statement.GetValue(0).AsText());
        return values;
    }

    private static byte[] ReadBlob(IManagedConnectionAdapter connection, string sql)
    {
        using var statement = connection.Prepare(sql);
        statement.Step().Should().Be(StatementStepResult.Row);
        return statement.GetValue(0).AsBlob().ToArray();
    }
}
