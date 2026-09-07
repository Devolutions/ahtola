using Ahtola.Core;
using AwesomeAssertions;
using MsData = Microsoft.Data.Sqlite;

namespace Ahtola.Tests;

public class ManagedSqlBindingParityTests
{
    [Test]
    public void JournalModeResolutionStaysWithTheCallingConnection()
    {
        using var database = new EmbeddedDatabase();
        using var first = database.Connect();
        Execute(first, "ATTACH ':memory:' AS aux; PRAGMA aux.journal_mode=mvcc;", default);
        using var second = database.Connect();

        using var statement = first.Prepare("SELECT journal_mode FROM pragma_journal_mode(?1);");
        statement.Bind(1, SqlValue.Text("aux"));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Should().Be(SqlValue.Text("mvcc"));
    }

    [Test]
    public void BundledSqliteDistinguishesDeclaredBlobFromLiteralCteAffinity()
    {
        using var connection = new MsData.SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE blobcol(x BLOB);
            INSERT INTO blobcol VALUES (3);
            CREATE TABLE t4(a TEXT);
            INSERT INTO t4 VALUES ('3');
            """;
        command.ExecuteNonQuery();

        command.CommandText = """
            WITH u(x) AS (SELECT x FROM blobcol UNION ALL SELECT 'unused_arm')
            SELECT a FROM t4 WHERE a IN (SELECT x FROM u);
            """;
        command.ExecuteScalar().Should().BeNull();

        command.CommandText = """
            WITH u(x) AS (SELECT 3 UNION ALL SELECT 'unused_arm')
            SELECT a FROM t4 WHERE a IN (SELECT x FROM u);
            """;
        command.ExecuteScalar().Should().Be("3");
        TestContext.Out.WriteLine($"Reference SQLite version: {connection.ServerVersion}");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CorrelatedInKeepsTheOuterOperandScope(bool cancelable)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var cancellation = new CancellationTokenSource();
        var token = cancelable ? cancellation.Token : default;
        Execute(connection, """
            CREATE TABLE outer_rows(id INTEGER, amount INTEGER, key1 INTEGER);
            CREATE TABLE inner_rows(amount INTEGER, key1 INTEGER);
            INSERT INTO outer_rows VALUES (1,100,1),(2,NULL,1),(3,5,1),(4,5,99);
            INSERT INTO inner_rows VALUES (5,1),(NULL,1);
            """, token);

        ReadRows(connection, """
            SELECT id FROM outer_rows o
            WHERE amount IN (SELECT i.amount FROM inner_rows i WHERE i.key1=o.key1)
            ORDER BY id;
            """, token).Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OuterAndLocalAggregateOwnershipPreserveCardinality(bool cancelable)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var cancellation = new CancellationTokenSource();
        var token = cancelable ? cancellation.Token : default;
        Execute(connection, """
            CREATE TABLE t(a);
            INSERT INTO t VALUES (1),(2),(3);
            CREATE TABLE u(b);
            INSERT INTO u VALUES (10),(20);
            """, token);

        ReadRows(connection, "SELECT (SELECT count(t.a)) FROM t;", token)
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3));
        ReadRows(connection, "SELECT (SELECT count(t.a) FROM u) FROM t;", token)
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(3));
        ReadRows(connection, "SELECT (SELECT count(t.a)) FROM t WHERE a>1;", token)
            .Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(2));

        var local = ReadRows(
            connection, "SELECT a,(SELECT count(u.b) FROM u) FROM t ORDER BY a;", token);
        local.Should().HaveCount(3);
        for (var index = 0; index < local.Length; index++)
            local[index].Should().Equal(SqlValue.Integer(index + 1), SqlValue.Integer(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FullJoinCorrelationPreservesNullExtendedRows(bool cancelable)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var cancellation = new CancellationTokenSource();
        var token = cancelable ? cancellation.Token : default;
        Execute(connection, """
            CREATE TABLE l(id INTEGER);
            CREATE TABLE r(id INTEGER);
            CREATE TABLE s(id INTEGER);
            INSERT INTO l VALUES (1),(2);
            INSERT INTO r VALUES (2),(3);
            INSERT INTO s VALUES (1),(3);
            """, token);

        ReadRows(connection, """
            SELECT l.id,r.id FROM l FULL JOIN r ON l.id=r.id
            WHERE EXISTS(SELECT 1 FROM s WHERE s.id=l.id)
            ORDER BY coalesce(l.id,r.id);
            """, token).Should().ContainSingle().Which.Should().Equal(SqlValue.Integer(1), SqlValue.Null);

        var absent = ReadRows(connection, """
            SELECT l.id,r.id FROM l FULL JOIN r ON l.id=r.id
            WHERE NOT EXISTS(SELECT 1 FROM s WHERE s.id=l.id)
            ORDER BY coalesce(l.id,r.id);
            """, token);
        absent.Should().HaveCount(2);
        absent[0].Should().Equal(SqlValue.Integer(2), SqlValue.Integer(2));
        absent[1].Should().Equal(SqlValue.Null, SqlValue.Integer(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeclaredBlobAndAbsentAffinityRemainDistinct(bool cancelable)
    {
        using var database = new EmbeddedDatabase();
        using var connection = database.Connect();
        using var cancellation = new CancellationTokenSource();
        var token = cancelable ? cancellation.Token : default;
        Execute(connection, """
            CREATE TABLE blobcol(x BLOB);
            INSERT INTO blobcol VALUES (3);
            CREATE TABLE t4(a TEXT);
            INSERT INTO t4 VALUES ('3');
            """, token);

        ReadRows(connection, """
            WITH u(x) AS (SELECT x FROM blobcol UNION ALL SELECT 'unused_arm')
            SELECT a FROM t4 WHERE a IN (SELECT x FROM u);
            """, token).Should().BeEmpty();

        ReadRows(connection, """
            WITH u(x) AS (SELECT 3 UNION ALL SELECT 'unused_arm')
            SELECT a FROM t4 WHERE a IN (SELECT x FROM u);
            """, token).Should().ContainSingle().Which.Should().Equal(SqlValue.Text("3"));
    }

    private static void Execute(EmbeddedConnection connection, string sql, CancellationToken token)
    {
        foreach (var statement in connection.PrepareScript(sql))
        {
            using (statement)
            {
                while (statement.Step(token) == StatementStepResult.Row)
                {
                }
            }
        }
    }

    private static SqlValue[][] ReadRows(
        EmbeddedConnection connection, string sql, CancellationToken token)
    {
        using var statement = connection.Prepare(sql);
        var rows = new List<SqlValue[]>();
        while (statement.Step(token) == StatementStepResult.Row)
        {
            rows.Add(Enumerable.Range(0, statement.GetColumnCount())
                .Select(statement.GetValue).ToArray());
        }

        return rows.ToArray();
    }
}
